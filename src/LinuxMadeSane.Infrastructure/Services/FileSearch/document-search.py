# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
import datetime, fnmatch, json, os, re, shutil, signal, stat, subprocess, sys, time
request = json.loads(sys.argv[1])
root = request["RootPath"]
pattern = (request.get("NamePattern") or "").strip()
contains_text = (request.get("ContainsText") or "").strip()
case_insensitive = bool(request.get("CaseInsensitive", True))
max_results = max(1, min(int(request.get("MaxResults", 500)), 2000))
results = []
pending_pdf_files = []
limit_reached = False
content_types = int(request.get("ContentTypes", 7))
warnings = set()
pdf_search_support_required = False
skipped = 0
active = []
deadline = time.monotonic() + 570
last_progress = 0
grep = shutil.which("grep")
pdftext = shutil.which("pdftotext")
iconv = shutil.which("iconv")
candidate_names = request.get("CandidateNames")
if candidate_names is not None and (not isinstance(candidate_names, list) or len(candidate_names) > 64 or
        any(not isinstance(name, str) or not name or name in (".", "..") or "/" in name or "\0" in name for name in candidate_names)):
    raise SystemExit("Search up to 64 files from the current folder per request.")
if contains_text and (len(contains_text) > 512 or any(c in contains_text for c in "\r\n\0")):
    raise SystemExit("Enter a single word or phrase of up to 512 characters.")
if contains_text and (not content_types or content_types & ~7):
    raise SystemExit("Select PDF, text or log files to search.")
if contains_text and not grep:
    raise SystemExit("Content search needs grep installed on the host being searched.")

def stop_children():
    for child in active:
        if child.poll() is None:
            child.kill()
    for child in active:
        child.wait()
    active.clear()

def cancelled(signum, frame):
    stop_children()
    raise SystemExit(130)

signal.signal(signal.SIGTERM, cancelled)
signal.signal(signal.SIGINT, cancelled)
signal.signal(signal.SIGHUP, cancelled)

def constrain_child():
    # One document at a time. Bounds apply to grep and PDF extraction independently.
    try:
        import resource
        resource.setrlimit(resource.RLIMIT_AS, (256 * 1024 * 1024, 256 * 1024 * 1024))
        resource.setrlimit(resource.RLIMIT_CPU, (15, 15))
        os.nice(10)
    except (ImportError, OSError, ValueError):
        pass

text_extensions = {".txt", ".text", ".md", ".rst", ".csv", ".tsv", ".json", ".jsonl",
                   ".xml", ".html", ".htm", ".yaml", ".yml", ".toml", ".ini", ".conf",
                   ".cfg", ".sh", ".bash", ".py", ".sql", ".css", ".js", ".ts"}

def content_kind(path):
    name = os.path.basename(path).lower()
    if name.endswith(".pdf"):
        return 1
    if re.search(r"\.log(?:\.\d+)?$", name) or re.fullmatch(r"(?:syslog|messages|dmesg)(?:\.\d+)?", name):
        return 4
    return 2 if os.path.splitext(name)[1] in text_extensions else 0

def report_progress(path, match=None):
    global last_progress
    if len(sys.argv) < 3 or sys.argv[2] != "progress":
        return
    if match is not None:
        sys.stderr.write("LMS_MATCH\t" + json.dumps(match, ensure_ascii=False) + "\n")
    elif time.monotonic() - last_progress >= 0.3:
        # JSON keeps tabs/newlines in unusual filenames out of the framing.
        sys.stderr.write("LMS_PROGRESS_JSON\t" + json.dumps(path) + "\n")
        last_progress = time.monotonic()
    else:
        return
    sys.stderr.flush()

def timestamp(value):
    if not value:
        return None
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()

modified_from = timestamp(request.get("ModifiedFromUtc"))
modified_to = timestamp(request.get("ModifiedToUtc"))
created_from = timestamp(request.get("CreatedFromUtc"))
created_to = timestamp(request.get("CreatedToUtc"))
accessed_from = timestamp(request.get("AccessedFromUtc"))
accessed_to = timestamp(request.get("AccessedToUtc"))

def name_matches(name):
    literal = request.get("NameContains")
    if literal and literal.casefold() not in name.casefold():
        return False
    if not pattern:
        return True
    candidate = name.casefold() if case_insensitive else name
    expected = pattern.casefold() if case_insensitive else pattern
    if "*" in expected or "?" in expected:
        return fnmatch.fnmatchcase(candidate, expected)
    return expected in candidate

def file_contains_text(path):
    global skipped, pdf_search_support_required
    kind = content_kind(path)
    if not kind or not content_types & kind:
        return False
    report_progress(path)
    try:
        # Do not follow a symlink or block on a FIFO if a file is replaced during traversal.
        fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW)
        with os.fdopen(fd, "rb") as stream:
            if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                return False
            sample = stream.read(65536)
            is_pdf = b"%PDF-" in sample[:1024]
            encoding = ("UTF-32" if sample.startswith((b"\xff\xfe\0\0", b"\0\0\xfe\xff")) else
                        "UTF-16" if sample.startswith((b"\xff\xfe", b"\xfe\xff")) else None)
            if kind == 1 and not is_pdf:
                skipped += 1
                return False
            if kind != 1 and not encoding and (is_pdf or b"\0" in sample or any(c < 9 or 13 < c < 32 for c in sample)):
                return False
            if kind == 1 and not pdftext:
                pdf_search_support_required = True
                return None
            stream.seek(0)
            if encoding and not iconv:
                skipped += 1
                return False
            options = [grep, "-F", "-I", "-q"] + (["-i"] if case_insensitive else []) + ["-e", contains_text]
            environment = dict(os.environ)
            # A consistent Unicode locale for Linux tools; never treat binary input as text.
            if sys.platform.startswith("linux"):
                environment["LC_ALL"] = "C.UTF-8"
            converter = None
            if kind == 1:
                # Extract to a pipe, not images, temporary text files or an in-memory document.
                converter = subprocess.Popen([pdftext, "-q", "-enc", "UTF-8", "-nopgbrk", "/dev/fd/" + str(stream.fileno()), "-"],
                    stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                    pass_fds=(stream.fileno(),), preexec_fn=constrain_child)
                active.append(converter)
                source = converter.stdout
            elif encoding:
                converter = subprocess.Popen([iconv, "-f", encoding, "-t", "UTF-8"], stdin=stream,
                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, preexec_fn=constrain_child)
                active.append(converter)
                source = converter.stdout
            else:
                source = stream
            matcher = subprocess.Popen(options, stdin=source, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, env=environment, preexec_fn=constrain_child)
            active.append(matcher)
            if converter is not None:
                source.close()
            result = matcher.wait(timeout=min(20, max(0.1, deadline - time.monotonic())))
            if result == 0:
                return True  # First match is enough; finally stops the extractor immediately.
            if result != 1 or (converter is not None and converter.wait(timeout=1) != 0):
                skipped += 1
            return False
    except (OSError, subprocess.TimeoutExpired):
        skipped += 1
        return False
    finally:
        stop_children()

if not os.path.isdir(root):
    raise SystemExit("The search folder was not found or is not accessible.")
# An inline browser filter supplies exact displayed names. Never walk unrelated files
# or descend into subfolders for this case, including when that list is empty.
entries_to_search = [(root, [], candidate_names)] if candidate_names is not None else os.walk(root, followlinks=False)
for current_path, directory_names, file_names in entries_to_search:
    if time.monotonic() >= deadline:
        warnings.add("The search time limit was reached. Narrow the folder or filters and search again.")
        break
    report_progress(current_path)
    entries = [(name, True) for name in directory_names] + [(name, False) for name in file_names]
    if not request.get("IncludeSubfolders", True):
        directory_names[:] = []
    for name, hinted_directory in entries:
        if time.monotonic() >= deadline:
            warnings.add("The search time limit was reached. Narrow the folder or filters and search again.")
            break
        full_path = os.path.join(current_path, name)
        try:
            info = os.lstat(full_path)
        except OSError:
            continue
        is_link = stat.S_ISLNK(info.st_mode)
        is_directory = stat.S_ISDIR(info.st_mode) and not is_link
        if is_directory and not request.get("IncludeFolders", True):
            continue
        if not is_directory and not request.get("IncludeFiles", True):
            continue
        if not name_matches(name):
            continue
        if modified_from is not None and info.st_mtime < modified_from:
            continue
        if modified_to is not None and info.st_mtime > modified_to:
            continue
        created_at = getattr(info, "st_birthtime", info.st_ctime)
        if created_from is not None and created_at < created_from:
            continue
        if created_to is not None and created_at > created_to:
            continue
        if accessed_from is not None and info.st_atime < accessed_from:
            continue
        if accessed_to is not None and info.st_atime > accessed_to:
            continue
        minimum_size = request.get("MinimumSizeBytes")
        maximum_size = request.get("MaximumSizeBytes")
        if is_directory and (minimum_size is not None or maximum_size is not None or contains_text):
            continue
        if not is_directory and minimum_size is not None and info.st_size < int(minimum_size):
            continue
        if not is_directory and maximum_size is not None and info.st_size > int(maximum_size):
            continue
        pending_pdf = False
        if contains_text:
            if not stat.S_ISREG(info.st_mode):
                continue
            matched = file_contains_text(full_path)
            pending_pdf = matched is None
            if not matched and not pending_pdf:
                continue
        link_target = ""
        if is_link:
            try:
                link_target = os.readlink(full_path)
            except OSError:
                pass
        match = {
            "Name": name,
            "FullPath": full_path,
            "ParentPath": current_path,
            "ItemType": 2 if is_link else (1 if is_directory else 0),
            "SizeBytes": info.st_size,
            "LastModifiedUtc": datetime.datetime.fromtimestamp(info.st_mtime, datetime.timezone.utc).isoformat(),
            "MatchedContents": bool(contains_text) and not pending_pdf,
            "LinkTarget": link_target
        }
        if pending_pdf:
            pending_pdf_files.append(match)
            if len(pending_pdf_files) >= 2000:
                limit_reached = True
                break
            continue
        results.append(match)
        report_progress(current_path, results[-1])
        if len(results) >= max_results:
            limit_reached = True
            break
    if limit_reached:
        break

results.sort(key=lambda item: item["FullPath"].casefold())
if skipped:
    warnings.add(str(skipped) + " document" + ("s" if skipped != 1 else "") + " could not be searched (unreadable, protected, damaged or timed out). Other files were searched.")
print(json.dumps({"RootPath": root, "Results": results, "LimitReached": limit_reached,
                  "PdfSearchSupportRequired": pdf_search_support_required,
                  "PendingPdfFiles": pending_pdf_files,
                  "Warning": " ".join(sorted(warnings)) or None}, ensure_ascii=False))
