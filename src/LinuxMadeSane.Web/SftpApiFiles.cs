// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;
using LinuxMadeSane.Infrastructure.Persistence.Entities;

namespace LinuxMadeSane.Web;

internal static class SftpApiFiles
{
    internal const int FileLimit = 50 * 1024 * 1024;
    internal const int RequestLimit = FileLimit + 1024 * 1024;
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly string Helper = ReadHelper();

    private static string ReadHelper()
    {
        using var reader = new StreamReader(typeof(SftpApiFiles).Assembly.GetManifestResourceStream("Lms.SftpApi.Files")!);
        return reader.ReadToEnd();
    }

    internal static async Task<IResult> ExecuteAsync(HttpContext c, SftpManagedUser user, string operation)
    {
        if (!await Slots.WaitAsync(0, c.RequestAborted)) return Results.StatusCode(429);
        FileStream? transfer = null;
        bool sendingDownload = false;
        try
        {
            var path = (string?)c.Request.Query["path"] ?? "";
            IFormFile? upload = null;
            if (operation is "upload" or "replace")
            {
                if (!c.Request.HasFormContentType) return Results.StatusCode(415);
                if (c.Request.ContentLength > RequestLimit) return Results.StatusCode(413);
                var form = await c.Request.ReadFormAsync(c.RequestAborted);
                var file = form.Files.GetFile("file");
                if (file == null || form.Files.Count != 1) return Results.BadRequest("Supply one multipart field named file.");
                if (file.Length > FileLimit) return Results.StatusCode(413);
                if (path.Length == 0 && operation == "upload") path = Path.GetFileName(file.FileName.Replace('\\','/'));
                upload = file;
            }
            if (path.Length > 4096 || path.Contains('\\') || path.Any(char.IsControl) ||
                (path.Length == 0 ? operation != "list" : path.Split('/').Any(p => p is "" or "." or ".." || p.Length > 255)))
                return Results.BadRequest("Use a path relative to the account’s files folder.");
            if (upload != null || operation == "download")
            {
                if (!OperatingSystem.IsLinux()) return Results.StatusCode(503);
                transfer = new FileStream(Path.Combine(Path.GetTempPath(), $"lms-sftp-{Guid.NewGuid():N}"),
                    new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose });
                if (upload != null)
                {
                    await upload.CopyToAsync(transfer, c.RequestAborted);
                    await transfer.FlushAsync(c.RequestAborted);
                }
            }
            var runner = c.RequestServices.GetRequiredService<ILinuxCommandRunner>();
            var cmd = new LinuxCommandRequest("/usr/bin/python3", ["-I", "-c", Helper, user.UserName, user.Folder.ChrootPath,
                operation, path, transfer?.Name ?? ""], true, TimeSpan.FromMinutes(2), $"SFTP API {operation}");
            var result = await runner.RunAsync(cmd, false, c.RequestAborted);
            var audit = c.RequestServices.GetRequiredService<ISftpAuditService>();
            await audit.RecordAsync(new(Guid.NewGuid(), $"api.files.{operation}", "user", user.UserName, $"File {operation}",
                $"API client {c.Items[typeof(SftpApiClient)]}", result.ExitCode == 0, DateTimeOffset.UtcNow, null), CancellationToken.None);
            if (result.ExitCode != 0)
                return Results.StatusCode(result.ExitCode switch { 3 => 404, 4 => 409, 5 => 403, 6 => 413, _ => 503 });
            if (operation == "download")
            {
                transfer!.Position = 0;
                var download = Results.File(transfer, "application/octet-stream", Path.GetFileName(path));
                c.Response.OnCompleted(() => { Slots.Release(); return Task.CompletedTask; });
                sendingDownload = true;
                transfer = null; // The response disposes the stream and removes the temporary file.
                return download;
            }
            return operation switch
            {
                "list" => Results.Text(result.StandardOutput, "application/json"),
                "upload" => Results.Json(new { path }, statusCode: 201),
                _ => Results.NoContent()
            };
        }
        catch (InvalidDataException) { return Results.BadRequest("Invalid multipart upload."); }
        finally
        {
            if (transfer != null) await transfer.DisposeAsync();
            if (!sendingDownload) Slots.Release();
        }
    }
}
