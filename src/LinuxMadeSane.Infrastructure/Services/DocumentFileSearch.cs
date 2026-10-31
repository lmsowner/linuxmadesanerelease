// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Diagnostics;
using System.Text.Json;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Infrastructure.Services;

/// <summary>Existing file search traversal shared by local, SSH and sudo searches; grep/Poppler do content matching.</summary>
internal static class DocumentFileSearch
{
    public static readonly string Script = ReadScript();
    private static readonly SemaphoreSlim LocalSearchGate = new(1);

    private static string ReadScript()
    {
        using var stream = typeof(DocumentFileSearch).Assembly.GetManifestResourceStream(
            "LinuxMadeSane.Infrastructure.Services.FileSearch.document-search.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void Validate(FileSearchRequest request)
    {
        if (request.CandidateNames is { } names && (names.Count > 64 || names.Any(name =>
            string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))))
            throw new InvalidOperationException("Search up to 64 files from the current folder per request.");
        if (!request.RequiresContentScan) return;
        if (request.ContainsText!.Length > 512 || request.ContainsText.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("Enter a single word or phrase of up to 512 characters.");
        if (request.ContentTypes == FileSearchContentTypes.None || (request.ContentTypes & ~FileSearchContentTypes.All) != 0)
            throw new InvalidOperationException("Select PDF, text or log files to search.");
    }

    public static string Command(FileSearchRequest request)
    {
        Validate(request);
        return "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for file search.' >&2; exit 127; }\n" +
            "exec python3 - " + Quote(JsonSerializer.Serialize(request)) + " progress <<'LMS_FILE_SEARCH'\n" + Script + "\nLMS_FILE_SEARCH";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static bool ReportProgress(string line, IProgress<FileSearchProgress>? progress)
    {
        if (line.StartsWith("LMS_PROGRESS_JSON\t", StringComparison.Ordinal))
        {
            var path = JsonSerializer.Deserialize<string>(line[18..]);
            if (path is not null) progress?.Report(new(path, DateTimeOffset.UtcNow));
            return true;
        }
        if (line.StartsWith("LMS_MATCH\t", StringComparison.Ordinal))
        {
            var match = JsonSerializer.Deserialize<FileSearchMatch>(line[10..]);
            if (match is not null) progress?.Report(new(match.ParentPath, DateTimeOffset.UtcNow, match));
            return true;
        }
        return false;
    }

    public static async Task<FileSearchResponse> RunLocalAsync(FileSearchRequest request,
        IProgress<FileSearchProgress>? progress, CancellationToken cancellationToken)
    {
        Validate(request);
        await LocalSearchGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var process = new Process { StartInfo = new("python3")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-c", Script, JsonSerializer.Serialize(request), "progress" }
            } };
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Content search needs python3 installed on the host being searched."); }
            using var stop = timeout.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = ReadErrorsAsync();
            async Task<string> ReadErrorsAsync()
            {
                var message = "";
                while (await process.StandardError.ReadLineAsync(timeout.Token) is { } line)
                    if (!ReportProgress(line, progress) && message.Length < 4000) message += line + "\n";
                return message;
            }
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            if (process.ExitCode != 0) throw new InvalidOperationException((await errors).Trim() is { Length: > 0 } detail ? detail : "Document search could not complete.");
            return JsonSerializer.Deserialize<FileSearchResponse>(await output) ?? new(request.RootPath, [], false);
        }
        finally { LocalSearchGate.Release(); }
    }
}
