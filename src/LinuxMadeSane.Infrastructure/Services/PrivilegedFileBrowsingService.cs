// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Renci.SshNet;

namespace LinuxMadeSane.Infrastructure.Services;

// Runs the small set of filesystem primitives needed by a sudo file-browser session.
// Paths are always passed as process arguments and file contents over stdin; neither is
// interpolated into the Python program.
public sealed class PrivilegedFileBrowsingService(
    ILinuxCommandRunner commandRunner,
    ILocalFileBrowsingService localFileBrowsingService,
    ISftpFileBrowsingService sftpFileBrowsingService,
    ManagedHostSshConnectionFactory sshConnectionFactory,
    ITransientConnectionSecretStore transientConnectionSecretStore)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string ListItemsScript =
        """
        import datetime, grp, json, os, pwd, stat, sys
        path = sys.argv[1]
        if not os.path.isdir(path):
            raise FileNotFoundError(path)
        result = []
        with os.scandir(path) as entries:
            for entry in entries:
                full_path = os.path.join(path, entry.name)
                info = entry.stat(follow_symlinks=False)
                mode = info.st_mode
                is_link = stat.S_ISLNK(mode)
                item_type = 2 if is_link else (1 if stat.S_ISDIR(mode) else 0)
                try:
                    owner = pwd.getpwuid(info.st_uid).pw_name
                except KeyError:
                    owner = str(info.st_uid)
                try:
                    group = grp.getgrgid(info.st_gid).gr_name
                except KeyError:
                    group = str(info.st_gid)
                try:
                    link_target = os.readlink(full_path) if is_link else ""
                except OSError:
                    link_target = ""
                result.append({
                    "Name": entry.name,
                    "FullPath": full_path,
                    "ItemType": item_type,
                    "SizeBytes": info.st_size,
                    "LastModifiedUtc": datetime.datetime.fromtimestamp(info.st_mtime, datetime.timezone.utc).isoformat(),
                    "Permissions": stat.filemode(mode),
                    "OwnerName": owner,
                    "GroupName": group,
                    "PermissionsOctal": format(stat.S_IMODE(mode), "04o"),
                    "LinkTarget": link_target
                })
        print(json.dumps(result, ensure_ascii=False))
        """;

    private const string ReadFileScript =
        """
        import base64, datetime, json, os, sys
        path = sys.argv[1]
        limit = int(sys.argv[2])
        if os.path.isdir(path):
            raise IsADirectoryError(path)
        info = os.stat(path)
        with open(path, "rb") as stream:
            content = stream.read(limit)
        print(json.dumps({
            "ContentBase64": base64.b64encode(content).decode("ascii"),
            "SizeBytes": info.st_size,
            "LastModifiedUtc": datetime.datetime.fromtimestamp(info.st_mtime, datetime.timezone.utc).isoformat()
        }))
        """;

    private const string SearchScript =
        """
        import datetime, fnmatch, json, os, stat, sys
        request = json.loads(sys.argv[1])
        root = request["RootPath"]
        pattern = (request.get("NamePattern") or "").strip()
        contains_text = (request.get("ContainsText") or "").strip()
        case_insensitive = bool(request.get("CaseInsensitive", True))
        max_results = max(1, min(int(request.get("MaxResults", 500)), 2000))
        results = []
        limit_reached = False

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
            if not pattern:
                return True
            candidate = name.casefold() if case_insensitive else name
            expected = pattern.casefold() if case_insensitive else pattern
            if "*" in expected or "?" in expected:
                return fnmatch.fnmatchcase(candidate, expected)
            return expected in candidate

        def file_contains_text(path):
            expected = contains_text.casefold() if case_insensitive else contains_text
            carry = ""
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as stream:
                    while True:
                        chunk = stream.read(4096)
                        if not chunk:
                            return expected in (carry.casefold() if case_insensitive else carry)
                        if "\0" in chunk:
                            return False
                        candidate = carry + chunk
                        if expected in (candidate.casefold() if case_insensitive else candidate):
                            return True
                        carry = candidate[-max(0, len(expected) - 1):]
            except OSError:
                return False

        for current_path, directory_names, file_names in os.walk(root, followlinks=False):
            entries = [(name, True) for name in directory_names] + [(name, False) for name in file_names]
            for name, hinted_directory in entries:
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
                if created_from is not None and info.st_ctime < created_from:
                    continue
                if created_to is not None and info.st_ctime > created_to:
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
                if contains_text and (is_directory or is_link or not file_contains_text(full_path)):
                    continue
                link_target = ""
                if is_link:
                    try:
                        link_target = os.readlink(full_path)
                    except OSError:
                        pass
                results.append({
                    "Name": name,
                    "FullPath": full_path,
                    "ParentPath": current_path,
                    "ItemType": 2 if is_link else (1 if is_directory else 0),
                    "SizeBytes": info.st_size,
                    "LastModifiedUtc": datetime.datetime.fromtimestamp(info.st_mtime, datetime.timezone.utc).isoformat(),
                    "MatchedContents": bool(contains_text),
                    "LinkTarget": link_target
                })
                if len(results) >= max_results:
                    limit_reached = True
                    break
            if limit_reached:
                break

        results.sort(key=lambda item: item["FullPath"].casefold())
        print(json.dumps({"RootPath": root, "Results": results, "LimitReached": limit_reached}, ensure_ascii=False))
        """;

    private const string WriteFileScript =
        """
        import datetime, json, os, sys
        path = sys.argv[1]
        create_directories = sys.argv[2] == "1"
        parent = os.path.dirname(path)
        if parent and create_directories:
            os.makedirs(parent, exist_ok=True)
        elif parent and not os.path.isdir(parent):
            raise FileNotFoundError(parent)
        content = sys.stdin.buffer.read()
        with open(path, "wb") as stream:
            stream.write(content)
        info = os.stat(path)
        print(json.dumps({
            "SizeBytes": info.st_size,
            "LastModifiedUtc": datetime.datetime.fromtimestamp(info.st_mtime, datetime.timezone.utc).isoformat()
        }))
        """;

    private const string CreateDirectoryScript =
        """
        import os, sys
        path = sys.argv[1]
        if os.path.isfile(path):
            raise FileExistsError(path)
        os.makedirs(path, exist_ok=True)
        """;

    private const string DeleteScript =
        """
        import os, shutil, sys
        path = sys.argv[1]
        recursive = sys.argv[2] == "1"
        if os.path.islink(path) or os.path.isfile(path):
            os.unlink(path)
        elif os.path.isdir(path):
            if recursive:
                shutil.rmtree(path)
            else:
                os.rmdir(path)
        else:
            raise FileNotFoundError(path)
        """;

    private const string CopyScript =
        """
        import os, shutil, sys
        source, destination = sys.argv[1], sys.argv[2]
        if os.path.lexists(destination):
            raise FileExistsError(destination)
        if os.path.isdir(source) and not os.path.islink(source):
            shutil.copytree(source, destination, symlinks=True)
        elif os.path.lexists(source):
            os.makedirs(os.path.dirname(destination) or "/", exist_ok=True)
            shutil.copy2(source, destination, follow_symlinks=False)
        else:
            raise FileNotFoundError(source)
        """;

    private const string MoveScript =
        """
        import os, shutil, sys
        source, destination = sys.argv[1], sys.argv[2]
        if os.path.lexists(destination):
            raise FileExistsError(destination)
        if not os.path.lexists(source):
            raise FileNotFoundError(source)
        os.makedirs(os.path.dirname(destination) or "/", exist_ok=True)
        shutil.move(source, destination)
        """;

    private const string StageReadableFileScript =
        """
        import os, shutil, sys
        source, destination = sys.argv[1], sys.argv[2]
        shutil.copyfile(source, destination)
        os.chmod(destination, 0o600)
        sudo_uid = os.environ.get("SUDO_UID")
        sudo_gid = os.environ.get("SUDO_GID")
        if sudo_uid and sudo_gid:
            os.chown(destination, int(sudo_uid), int(sudo_gid))
        """;

    private const string InstallUploadedFileScript =
        """
        import os, shutil, sys
        source, destination = sys.argv[1], sys.argv[2]
        parent = os.path.dirname(destination)
        if not os.path.isdir(parent):
            raise FileNotFoundError(parent)
        with open(source, "rb") as input_stream, open(destination, "wb") as output_stream:
            shutil.copyfileobj(input_stream, output_stream, 1024 * 1024)
        """;

    private const string CreateArchiveScript =
        """
        import gzip, os, shutil, sys, tarfile, zipfile
        source, destination, archive_format = sys.argv[1], sys.argv[2], int(sys.argv[3])
        if not os.path.lexists(source):
            raise FileNotFoundError(source)
        if os.path.lexists(destination):
            raise FileExistsError(destination)
        parent = os.path.dirname(destination)
        if not os.path.isdir(parent):
            raise FileNotFoundError(parent)
        source_name = os.path.basename(source.rstrip("/"))
        source_parent = os.path.dirname(source.rstrip("/"))
        if archive_format == 0:
            with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                if os.path.isdir(source) and not os.path.islink(source):
                    for current_path, directory_names, file_names in os.walk(source):
                        relative_directory = os.path.relpath(current_path, source_parent)
                        if not directory_names and not file_names:
                            archive.writestr(relative_directory.rstrip("/") + "/", "")
                        for name in file_names:
                            full_path = os.path.join(current_path, name)
                            archive.write(full_path, os.path.relpath(full_path, source_parent))
                else:
                    archive.write(source, source_name)
        elif archive_format == 2 and not os.path.isdir(source):
            with open(source, "rb") as input_stream, gzip.open(destination, "wb") as output_stream:
                shutil.copyfileobj(input_stream, output_stream, 1024 * 1024)
        elif archive_format in (2, 3):
            with tarfile.open(destination, "w:gz") as archive:
                archive.add(source, arcname=source_name, recursive=True)
        else:
            raise ValueError("Unsupported Python archive format")
        """;

    private const string ExtractArchiveScript =
        """
        import gzip, os, shutil, sys, tarfile, zipfile
        archive_path, destination, archive_format = sys.argv[1], sys.argv[2], int(sys.argv[3])
        if not os.path.isfile(archive_path):
            raise FileNotFoundError(archive_path)
        if os.path.isdir(destination) and os.listdir(destination):
            raise FileExistsError(destination)
        os.makedirs(destination, exist_ok=True)
        if archive_format == 0:
            with zipfile.ZipFile(archive_path, "r") as archive:
                archive.extractall(destination)
        elif archive_format == 2:
            output_name = os.path.basename(archive_path)
            if output_name.lower().endswith(".gz"):
                output_name = output_name[:-3]
            with gzip.open(archive_path, "rb") as input_stream, open(os.path.join(destination, output_name), "wb") as output_stream:
                shutil.copyfileobj(input_stream, output_stream, 1024 * 1024)
        elif archive_format == 3:
            with tarfile.open(archive_path, "r:gz") as archive:
                archive.extractall(destination, filter="data")
        else:
            raise ValueError("Unsupported Python archive format")
        """;

    public async Task<ManagedHostConnectionValidationResult> ValidateAccessAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ExecuteAsync(
                host,
                connectionProfile,
                "/usr/bin/python3",
                ["-c", "import os; assert os.geteuid() == 0"],
                null,
                cancellationToken);
            return new ManagedHostConnectionValidationResult(true, string.Empty);
        }
        catch (Exception exception)
        {
            return new ManagedHostConnectionValidationResult(
                false,
                $"Sudo access is unavailable for {host.Name}: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        var output = await ExecutePythonAsync(
            host,
            connectionProfile,
            ListItemsScript,
            [normalizedPath],
            null,
            cancellationToken);

        var items = JsonSerializer.Deserialize<SftpItem[]>(output, JsonOptions) ?? [];
        return items
            .OrderByDescending(item => item.ItemType == SftpItemType.Folder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var binary = await ReadBinaryFileAsync(host, path, connectionProfile, Math.Clamp(maxBytes, 1, 1_048_576), cancellationToken);
        var decoded = TextFileEncoding.Decode(binary.ContentBytes);
        return new SftpFileContent(
            binary.FullPath,
            decoded.Content,
            binary.SizeBytes,
            binary.LastModifiedUtc,
            binary.IsTruncated,
            decoded.EncodingName);
    }

    public async Task<FileSearchResponse> SearchAsync(
        ManagedHost host,
        FileSearchRequest request,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            RootPath = NormalizePath(request.RootPath),
            NamePattern = request.NamePattern?.Trim(),
            ContainsText = request.ContainsText?.Trim(),
            MaxResults = Math.Clamp(request.MaxResults, 1, 2_000)
        };
        var serializedRequest = JsonSerializer.Serialize(normalizedRequest);
        var output = await ExecutePythonAsync(
            host,
            connectionProfile,
            SearchScript,
            [serializedRequest],
            null,
            cancellationToken);
        var response = JsonSerializer.Deserialize<FileSearchResponse>(output, JsonOptions)
                       ?? new FileSearchResponse(normalizedRequest.RootPath, [], false);

        foreach (var match in response.Results)
        {
            progress?.Report(new FileSearchProgress(match.ParentPath, DateTimeOffset.UtcNow, match));
        }

        return response;
    }

    public async Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        var safeMaxBytes = Math.Clamp(maxBytes, 1, 67_108_864);
        var output = await ExecutePythonAsync(
            host,
            connectionProfile,
            ReadFileScript,
            [normalizedPath, safeMaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            null,
            cancellationToken);
        var payload = JsonSerializer.Deserialize<PrivilegedReadPayload>(output, JsonOptions)
                      ?? throw new InvalidOperationException("The privileged file read returned no content.");
        var bytes = Convert.FromBase64String(payload.ContentBase64);

        return new SftpBinaryFileContent(
            normalizedPath,
            bytes,
            payload.SizeBytes,
            payload.LastModifiedUtc,
            payload.SizeBytes > bytes.LongLength);
    }

    public async Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        ManagedHostConnectionProfile connectionProfile,
        bool createDirectories,
        string? encodingName,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeMutablePath(path);
        var bytes = TextFileEncoding.Encode(content, encodingName);
        var output = await ExecutePythonAsync(
            host,
            connectionProfile,
            WriteFileScript,
            [normalizedPath, createDirectories ? "1" : "0"],
            bytes,
            cancellationToken);
        var payload = JsonSerializer.Deserialize<PrivilegedWritePayload>(output, JsonOptions)
                      ?? throw new InvalidOperationException("The privileged file write returned no result.");

        return new SftpWriteResult(normalizedPath, payload.SizeBytes, payload.LastModifiedUtc);
    }

    public async Task<string> CreateDirectoryAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeMutablePath(path);
        await ExecutePythonAsync(
            host,
            connectionProfile,
            CreateDirectoryScript,
            [normalizedPath],
            null,
            cancellationToken);
        return normalizedPath;
    }

    public async Task DeleteAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeMutablePath(path);
        await ExecutePythonAsync(
            host,
            connectionProfile,
            DeleteScript,
            [normalizedPath, recursive ? "1" : "0"],
            null,
            cancellationToken);
    }

    public async Task<string> CopyAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourcePath = NormalizeMutablePath(sourcePath);
        var normalizedDestinationPath = NormalizeMutablePath(destinationPath);
        EnsureDistinctPaths(normalizedSourcePath, normalizedDestinationPath);
        await ExecutePythonAsync(
            host,
            connectionProfile,
            CopyScript,
            [normalizedSourcePath, normalizedDestinationPath],
            null,
            cancellationToken);
        return normalizedDestinationPath;
    }

    public async Task<string> MoveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourcePath = NormalizeMutablePath(sourcePath);
        var normalizedDestinationPath = NormalizeMutablePath(destinationPath);
        EnsureDistinctPaths(normalizedSourcePath, normalizedDestinationPath);
        await ExecutePythonAsync(
            host,
            connectionProfile,
            MoveScript,
            [normalizedSourcePath, normalizedDestinationPath],
            null,
            cancellationToken);
        return normalizedDestinationPath;
    }

    public async Task DownloadFileAsync(
        ManagedHost host,
        string sourcePath,
        string localDestinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourcePath = NormalizePath(sourcePath);
        if (AiLocalMachine.IsLocalMachine(host.Id) && !connectionProfile.UseSshTransport)
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                StageReadableFileScript,
                [normalizedSourcePath, Path.GetFullPath(localDestinationPath)],
                null,
                cancellationToken);
            var size = new FileInfo(localDestinationPath).Length;
            progress?.Report(new FileTransferProgress(size, size));
            return;
        }

        var stagePath = $"/tmp/lms-file-browser-download-{Guid.NewGuid():N}";
        try
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                StageReadableFileScript,
                [normalizedSourcePath, stagePath],
                null,
                cancellationToken);
            var request = BuildRemoteRequest(host, connectionProfile);
            await sftpFileBrowsingService.DownloadFileAsync(
                host,
                stagePath,
                localDestinationPath,
                request.Username,
                request.Password,
                request.PrivateKey,
                request.PrivateKeyPassphrase,
                request.PreferStoredCredentials,
                progress,
                cancellationToken);
        }
        finally
        {
            await TryDeleteStageAsync(host, connectionProfile, stagePath);
        }
    }

    public async Task UploadFileAsync(
        ManagedHost host,
        string localSourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localSourcePath))
        {
            throw new InvalidOperationException($"{localSourcePath} was not found on the local machine.");
        }

        var normalizedDestinationPath = NormalizeMutablePath(destinationPath);
        if (AiLocalMachine.IsLocalMachine(host.Id) && !connectionProfile.UseSshTransport)
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                InstallUploadedFileScript,
                [Path.GetFullPath(localSourcePath), normalizedDestinationPath],
                null,
                cancellationToken);
            var size = new FileInfo(localSourcePath).Length;
            progress?.Report(new FileTransferProgress(size, size));
            return;
        }

        var stagePath = $"/tmp/lms-file-browser-upload-{Guid.NewGuid():N}";
        try
        {
            var request = BuildRemoteRequest(host, connectionProfile);
            await sftpFileBrowsingService.UploadFileAsync(
                host,
                localSourcePath,
                stagePath,
                request.Username,
                request.Password,
                request.PrivateKey,
                request.PrivateKeyPassphrase,
                request.PreferStoredCredentials,
                progress,
                cancellationToken);
            await ExecutePythonAsync(
                host,
                connectionProfile,
                InstallUploadedFileScript,
                [stagePath, normalizedDestinationPath],
                null,
                cancellationToken);
        }
        finally
        {
            await TryDeleteStageAsync(host, connectionProfile, stagePath);
        }
    }

    public async Task<string> CreateArchiveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourcePath = NormalizeMutablePath(sourcePath);
        var normalizedDestinationPath = NormalizeMutablePath(destinationArchivePath);
        EnsureDistinctPaths(normalizedSourcePath, normalizedDestinationPath);

        if (format is ArchiveFormat.Zip or ArchiveFormat.Gzip or ArchiveFormat.TarGzip)
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                CreateArchiveScript,
                [normalizedSourcePath, normalizedDestinationPath, ((int)format).ToString()],
                null,
                cancellationToken);
            return normalizedDestinationPath;
        }

        var toolArguments = format switch
        {
            ArchiveFormat.SevenZip => new[] { "7z", "a", normalizedDestinationPath, normalizedSourcePath },
            ArchiveFormat.Rar => new[] { "rar", "a", "-r", normalizedDestinationPath, normalizedSourcePath },
            _ => throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} compression is not supported.")
        };
        await ExecuteAsync(
            host,
            connectionProfile,
            "/usr/bin/env",
            toolArguments,
            null,
            cancellationToken);
        return normalizedDestinationPath;
    }

    public async Task<string> ExtractArchiveAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedArchivePath = NormalizeMutablePath(archivePath);
        var normalizedDestinationPath = NormalizeMutablePath(destinationDirectoryPath);

        if (format is ArchiveFormat.Zip or ArchiveFormat.Gzip or ArchiveFormat.TarGzip)
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                ExtractArchiveScript,
                [normalizedArchivePath, normalizedDestinationPath, ((int)format).ToString()],
                null,
                cancellationToken);
            return normalizedDestinationPath;
        }

        var toolArguments = format switch
        {
            ArchiveFormat.SevenZip => new[] { "7z", "x", normalizedArchivePath, $"-o{normalizedDestinationPath}" },
            ArchiveFormat.Rar => new[] { "7z", "x", normalizedArchivePath, $"-o{normalizedDestinationPath}" },
            _ => throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} extraction is not supported.")
        };
        await ExecuteAsync(
            host,
            connectionProfile,
            "/usr/bin/env",
            toolArguments,
            null,
            cancellationToken);
        return normalizedDestinationPath;
    }

    public async Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        ManagedHost host,
        string archivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        var normalizedArchivePath = NormalizePath(archivePath);
        var stagePath = $"/tmp/lms-file-browser-archive-{Guid.NewGuid():N}{ArchiveFormatSupport.GetExtension(format, false)}";
        try
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                StageReadableFileScript,
                [normalizedArchivePath, stagePath],
                null,
                cancellationToken);

            if (AiLocalMachine.IsLocalMachine(host.Id) && !connectionProfile.UseSshTransport)
            {
                return await localFileBrowsingService.ListArchiveEntriesAsync(
                    "/",
                    stagePath,
                    format,
                    maxEntries,
                    cancellationToken);
            }

            var request = BuildRemoteRequest(host, connectionProfile);
            return await sftpFileBrowsingService.ListArchiveEntriesAsync(
                host,
                stagePath,
                format,
                request.Username,
                request.Password,
                request.PrivateKey,
                request.PrivateKeyPassphrase,
                request.PreferStoredCredentials,
                maxEntries,
                cancellationToken);
        }
        finally
        {
            await TryDeleteStageAsync(host, connectionProfile, stagePath);
        }
    }

    public async Task SetOwnershipAndPermissionsAsync(
        ManagedHost host,
        FileOwnershipPermissionsChangeRequest request,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeMutablePath(request.Path);
        var owner = request.OwnerName?.Trim() ?? string.Empty;
        var group = request.GroupName?.Trim() ?? string.Empty;
        var permissions = request.PermissionsOctal?.Trim() ?? string.Empty;
        if (owner.Length == 0 && group.Length == 0 && permissions.Length == 0)
        {
            throw new InvalidOperationException("Provide an owner, group, or octal permissions value before applying changes.");
        }

        if (owner.Length > 0 || group.Length > 0)
        {
            var ownership = group.Length == 0 ? owner : $"{owner}:{group}";
            var arguments = new List<string> { "chown" };
            if (request.Recursive)
            {
                arguments.Add("-R");
            }

            arguments.Add("--");
            arguments.Add(ownership);
            arguments.Add(normalizedPath);
            await ExecuteAsync(
                host,
                connectionProfile,
                "/usr/bin/env",
                arguments,
                null,
                cancellationToken);
        }

        if (permissions.Length > 0)
        {
            if (permissions.Length is < 3 or > 4 ||
                permissions.Any(character => character is < '0' or > '7'))
            {
                throw new InvalidOperationException("Permissions must be a 3 or 4 digit octal value such as 775 or 2775.");
            }

            var arguments = new List<string> { "chmod" };
            if (request.Recursive)
            {
                arguments.Add("-R");
            }

            arguments.Add("--");
            arguments.Add(permissions);
            arguments.Add(normalizedPath);
            await ExecuteAsync(
                host,
                connectionProfile,
                "/usr/bin/env",
                arguments,
                null,
                cancellationToken);
        }
    }

    private Task<string> ExecutePythonAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        string script,
        IReadOnlyList<string> arguments,
        byte[]? inputBytes,
        CancellationToken cancellationToken) =>
        ExecuteAsync(host, connectionProfile, "/usr/bin/python3", ["-c", script, .. arguments], inputBytes, cancellationToken);

    private async Task<string> ExecuteAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? inputBytes,
        CancellationToken cancellationToken)
    {
        PrivilegedCommandResult result;
        if (AiLocalMachine.IsLocalMachine(host.Id) && !connectionProfile.UseSshTransport)
        {
            var localResult = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    fileName,
                    arguments,
                    RequiresSudo: true,
                    CommandTimeout,
                    $"Run privileged file-browser operation on {host.Name}.",
                    host.DefaultWorkingDirectory)
                {
                    StandardInputBytes = inputBytes
                },
                dryRun: false,
                cancellationToken);
            result = new PrivilegedCommandResult(localResult.ExitCode, localResult.StandardOutput, localResult.StandardError);
        }
        else
        {
            result = await ExecuteRemoteAsync(host, connectionProfile, fileName, arguments, inputBytes, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? $"Privileged file access failed with exit code {result.ExitCode}."
                    : result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException(detail);
        }

        return result.StandardOutput;
    }

    private async Task<PrivilegedCommandResult> ExecuteRemoteAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? inputBytes,
        CancellationToken cancellationToken)
    {
        var request = BuildRemoteRequest(host, connectionProfile);
        var credentials = await sshConnectionFactory.ResolveCredentialsAsync(
            host,
            new ManagedHostSshCredentialRequest(
                request.Username,
                request.Password,
                request.PrivateKey,
                request.PrivateKeyPassphrase,
                request.PreferStoredCredentials),
            cancellationToken);
        using var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);
        client.Connect();

        try
        {
            var commandText = "sudo -n " + QuoteShellArgument(fileName) +
                              string.Concat(arguments.Select(argument => " " + QuoteShellArgument(argument)));
            using var command = client.CreateCommand(commandText);
            command.CommandTimeout = CommandTimeout;
            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    try
                    {
                        ((SshCommand)state!).CancelAsync();
                    }
                    catch
                    {
                    }
                },
                command);

            var asyncResult = command.BeginExecute();
            if (inputBytes is not null)
            {
                using var input = command.CreateInputStream();
                await input.WriteAsync(inputBytes, cancellationToken);
                await input.FlushAsync(cancellationToken);
                input.Close();
            }

            var outputTask = new StreamReader(command.OutputStream, Encoding.UTF8).ReadToEndAsync(cancellationToken);
            var errorTask = new StreamReader(command.ExtendedOutputStream, Encoding.UTF8).ReadToEndAsync(cancellationToken);
            await Task.Run(() => command.EndExecute(asyncResult), cancellationToken);
            await Task.WhenAll(outputTask, errorTask);

            return new PrivilegedCommandResult(
                command.ExitStatus ?? -1,
                outputTask.Result,
                errorTask.Result);
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private ManagedHostConnectionRequest BuildRemoteRequest(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile)
    {
        var secrets = transientConnectionSecretStore.Get(connectionProfile.SecretHandle);
        return new ManagedHostConnectionRequest(
            string.IsNullOrWhiteSpace(connectionProfile.Username) ? host.Username : connectionProfile.Username.Trim(),
            NullIfEmpty(secrets.Password),
            NullIfEmpty(secrets.PrivateKey),
            NullIfEmpty(secrets.PrivateKeyPassphrase),
            connectionProfile.PreferStoredCredentials);
    }

    private async Task TryDeleteStageAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        string stagePath)
    {
        try
        {
            await ExecutePythonAsync(
                host,
                connectionProfile,
                DeleteScript,
                [stagePath, "0"],
                null,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sudo file access requires an absolute path.");
        }

        if (normalized.Contains('\0'))
        {
            throw new InvalidOperationException("The requested path is invalid.");
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static string NormalizeMutablePath(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == "/")
        {
            throw new InvalidOperationException("The filesystem root cannot be changed.");
        }

        return normalized;
    }

    private static void EnsureDistinctPaths(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source and destination paths must be different.");
        }

        if (destinationPath.StartsWith(sourcePath.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A directory cannot be copied or moved into itself.");
        }
    }

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record PrivilegedCommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record PrivilegedReadPayload(
        string ContentBase64,
        long SizeBytes,
        DateTimeOffset? LastModifiedUtc);

    private sealed record PrivilegedWritePayload(
        long SizeBytes,
        DateTimeOffset LastModifiedUtc);

    private sealed record ManagedHostConnectionRequest(
        string Username,
        string? Password,
        string? PrivateKey,
        string? PrivateKeyPassphrase,
        bool PreferStoredCredentials);
}
