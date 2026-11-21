// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Services;

internal static class ArchiveSelectionExtraction
{
    // Extract through directory descriptors: never follow archive or destination links,
    // and never replace existing files. An empty selection means the whole archive.
    public const string Script = """
        import contextlib, gzip, json, os, shutil, stat, subprocess, sys, tarfile, zipfile
        source, destination, selected_json = sys.argv[1:4]
        def clean(name):
            if '\\' in name or '\x00' in name or name.startswith('/'):
                raise ValueError('Unsafe archive path: ' + repr(name))
            parts = [p for p in name.split('/') if p not in ('', '.')]
            if '..' in parts or any(':' in p for p in parts):
                raise ValueError('Unsafe archive path: ' + repr(name))
            return '/'.join(parts)
        selected = [clean(p) for p in json.loads(selected_json)]
        if any(not p for p in selected): raise ValueError('Invalid archive selection')
        def wanted(name):
            return not selected or any(name == p or name.startswith(p + '/') for p in selected)
        archive = None
        if zipfile.is_zipfile(source):
            archive = zipfile.ZipFile(source)
            entries = [(clean(e.filename), e.is_dir(), e.file_size, lambda e=e: archive.open(e),
                        stat.S_IFMT(e.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR)) for e in archive.infolist()]
        elif source.lower().endswith(('.tar.gz', '.tgz')):
            archive = tarfile.open(source, 'r:gz')
            entries = [(clean(e.name), e.isdir(), e.size, lambda e=e: archive.extractfile(e), e.isfile() or e.isdir()) for e in archive.getmembers()]
        elif source.lower().endswith('.gz'):
            entries = [(clean(os.path.basename(source)[:-3]), False, 0, lambda: gzip.open(source, 'rb'), True)]
        elif source.lower().endswith(('.7z', '.rar')):
            tool = shutil.which('7z') or shutil.which('7zz')
            if not tool: raise ValueError('Install 7z on this machine to extract 7z or RAR contents.')
            listing = subprocess.run([tool, 'l', '-slt', '-ba', '--', source], stdin=subprocess.DEVNULL, capture_output=True, text=True, check=True).stdout
            @contextlib.contextmanager
            def read_seven(name):
                process = subprocess.Popen([tool, 'x', '-so', '-spd', '--', source, name], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                try:
                    yield process.stdout
                    process.stdout.close()
                    if process.wait() != 0: raise ValueError('7z failed to read ' + name)
                finally:
                    if process.poll() is None: process.kill()
                    process.wait()
            entries = []
            for block in listing.replace('\r\n', '\n').strip().split('\n\n'):
                fields = dict(line.split(' = ', 1) for line in block.splitlines() if ' = ' in line)
                if 'Path' not in fields: continue
                name = fields['Path']
                directory = fields.get('Folder') == '+' or 'D' in fields.get('Attributes', '').split(' ')[0]
                safe = not any(fields.get(key) for key in ('Symbolic Link', 'Hard Link')) and 'l' not in fields.get('Mode', '')
                entries.append((clean(name), directory, int(fields.get('Size', '0')), lambda name=name: read_seven(name), safe))
        else:
            raise ValueError('Unsupported archive format.')
        entries = [e for e in entries if e[0] and wanted(e[0])]
        if not entries: raise ValueError('No matching archive entries were found.')
        if any(not e[4] for e in entries): raise ValueError('Links and special files cannot be extracted by drag and drop.')
        if len(entries) > 100000 or sum(e[2] for e in entries) > 20 * 1024**3:
            raise ValueError('Archive selection exceeds the extraction limit (100,000 entries / 20 GiB).')
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        # Open each destination ancestor without following symlinks.
        root = os.open('/', flags)
        try:
            for part in os.path.abspath(destination).split('/'):
                if not part: continue
                child = os.open(part, flags, dir_fd=root)
                os.close(root)
                root = child
            # Preflight conflicts before writing any selected file.
            for name, directory, size, reader, safe in entries:
                fd = os.dup(root)
                try:
                    parts = name.split('/')
                    for i, part in enumerate(parts):
                        try: info = os.stat(part, dir_fd=fd, follow_symlinks=False)
                        except FileNotFoundError: break
                        if not stat.S_ISDIR(info.st_mode) or (i == len(parts)-1 and not directory):
                            raise ValueError('Destination already exists or is unsafe: ' + name)
                        child = os.open(part, flags, dir_fd=fd)
                        os.close(fd)
                        fd = child
                finally: os.close(fd)
            written = 0
            for name, directory, size, reader, safe in entries:
                fd = os.dup(root)
                try:
                    parts = name.split('/')
                    for part in parts if directory else parts[:-1]:
                        try: os.mkdir(part, dir_fd=fd)
                        except FileExistsError: pass
                        child = os.open(part, flags, dir_fd=fd)
                        os.close(fd)
                        fd = child
                    if not directory:
                        output = os.open(parts[-1], os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd)
                        try:
                            with os.fdopen(output, 'wb') as target, reader() as data:
                                while True:
                                    chunk = data.read(1024 * 1024)
                                    if not chunk: break
                                    written += len(chunk)
                                    if written > 20 * 1024**3: raise ValueError('Extraction exceeds 20 GiB.')
                                    target.write(chunk)
                        except BaseException:
                            os.unlink(parts[-1], dir_fd=fd)
                            raise
                finally: os.close(fd)
        finally:
            os.close(root)
            if archive: archive.close()
        """;
}
