// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Formats.Tar;
using System.IO.Compression;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class SelectedFileArchiveSupport
{
    public static string[] Validate(string baseDirectory, IReadOnlyList<string> paths, string destination, ArchiveFormat format)
    {
        if (format is not (ArchiveFormat.Zip or ArchiveFormat.TarGzip)) throw new NotSupportedException("Selected files support ZIP or tar.gz archives.");
        destination = Path.GetFullPath(destination);
        var root = Path.GetFullPath(baseDirectory).TrimEnd('/') + "/";
        var sources = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0) throw new InvalidOperationException("Select files or folders to archive.");
        foreach (var path in sources)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("Archive selections must be inside the search folder.");
            if (destination == path || destination.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Save the archive outside the selected files and folders.");
        }
        return sources.Where(path => !sources.Any(parent => parent != path && path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal))).ToArray();
    }

    public static async Task<string> CreateLocalAsync(string root, IReadOnlyList<string> sources, string destination, ArchiveFormat format, CancellationToken token)
    {
        sources = Validate(root, sources, destination, format);
        // CreateNew keeps existing archives intact, including on failure or cancellation.
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        try
        {
            IEnumerable<string> Entries(string path)
            {
                yield return path;
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) yield break;
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) foreach (var entry in Entries(child)) yield return entry;
            }
            if (format == ArchiveFormat.Zip)
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
                foreach (var path in sources.SelectMany(Entries))
                {
                    token.ThrowIfCancellationRequested();
                    var directory = Directory.Exists(path);
                    var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                    var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                    var entry = archive.CreateEntry(name + (directory && info.LinkTarget is null ? "/" : ""), CompressionLevel.Fastest);
                    if (info.LinkTarget is not null)
                    {
                        entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                        await using var data = entry.Open();
                        await data.WriteAsync(System.Text.Encoding.UTF8.GetBytes(info.LinkTarget), token);
                    }
                    else if (!directory)
                    {
                        await using var input = File.OpenRead(path); await using var data = entry.Open();
                        await input.CopyToAsync(data, 131072, token);
                    }
                }
            }
            else
            {
                await using var gzip = new GZipStream(output, CompressionLevel.Fastest, true);
                await using var archive = new TarWriter(gzip, TarEntryFormat.Pax, true);
                foreach (var path in sources.SelectMany(Entries))
                    await archive.WriteEntryAsync(path, Path.GetRelativePath(root, path).Replace('\\', '/'), token);
            }
            return destination;
        }
        catch { await output.DisposeAsync(); File.Delete(destination); throw; }
    }

    public const string PythonScript = """
        import json, os, stat, sys, tarfile, zipfile
        root, destination, archive_format, sources_json = sys.argv[1:5]
        sources = json.loads(sources_json)
        with open(destination, 'xb') as output:
            try:
                if int(archive_format) == 0:
                    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
                        def add(path):
                            name = os.path.relpath(path, root)
                            if os.path.islink(path):
                                entry = zipfile.ZipInfo(name)
                                entry.create_system = 3
                                entry.external_attr = (stat.S_IFLNK | 0o777) << 16
                                archive.writestr(entry, os.readlink(path))
                            elif os.path.isdir(path):
                                archive.write(path, name)
                                for child in os.scandir(path): add(child.path)
                            else: archive.write(path, name)
                        for source in sources: add(source)
                else:
                    with tarfile.open(fileobj=output, mode='w:gz') as archive:
                        for source in sources: archive.add(source, arcname=os.path.relpath(source, root), recursive=True)
            except BaseException:
                output.close()
                os.unlink(destination)
                raise
        """;
}
