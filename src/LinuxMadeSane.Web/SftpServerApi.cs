// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Sftp;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.SftpServer;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using LinuxMadeSane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Web;

internal static class SftpServerApi
{
    internal const string Prefix = "/api/sftpserver";
    internal sealed record AccountBody(string? Username, string? AuthenticationMode, string? Password, string? PublicKey);
    private static Guid Client(HttpContext c) => (Guid)c.Items[typeof(SftpApiClient)]!;
    private static object Summary(SftpManagedUser u) => new { username = u.UserName, authenticationMode = u.AuthenticationMode.ToString(), hasPublicKey = u.PublicKeys.Count > 0, enabled = u.IsEnabled };
    private static bool ValidName(string? name) => name != null && Regex.IsMatch(name, @"\A[a-z_][a-z0-9_-]{0,31}\z");
    private static IResult Error(int status, string detail) => Results.Problem(detail, statusCode: status);
    private static bool Mode(string? value, out SftpAuthenticationMode mode) =>
        Enum.TryParse(value, true, out mode) && Enum.GetName(mode)?.Equals(value, StringComparison.OrdinalIgnoreCase) == true;

    internal static void MapSftpServerApi(this WebApplication app)
    {
        var group = app.MapGroup(Prefix).AllowAnonymous();
        group.MapGet("/", () => Results.Ok(new { service = "LMS SFTP API", version = 1 }));
        group.MapGet("/server/status", async (ISftpServerInspectionService service, CancellationToken ct) =>
        {
            var s = await service.InspectAsync(ct);
            return Results.Ok(new { managed = s.Settings.IsManagedModeEnabled, sshActive = s.IsSshServiceActive, configurationValid = s.IsConfigurationValid });
        });
        group.MapGet("/users", async (HttpContext c, LinuxMadeSaneDbContext db) =>
        {
            var owner = Client(c);
            var accounts = await db.SftpManagedUsers.AsNoTracking().Where(u => u.ApiClientId == owner)
                .OrderBy(u => u.UserName).Select(u => new { username = u.UserName, u.AuthenticationMode,
                    hasPublicKey = u.PublicKeys.Any(), enabled = u.IsEnabled }).ToListAsync(c.RequestAborted);
            return Results.Ok(accounts.Select(u => new { u.username,
                authenticationMode = ((SftpAuthenticationMode)u.AuthenticationMode).ToString(), u.hasPublicKey, u.enabled }));
        });
        group.MapPost("/users", (HttpContext c, AccountBody body) => WithAccount(c, body.Username, "create", body));

        var users = group.MapGroup("/users/{username}");
        users.MapGet("", (HttpContext c, string username) => WithAccount(c, username, "read"));
        users.MapGet("/exists", (HttpContext c, string username) => WithAccount(c, username, "exists"));
        users.MapGet("/auth-guide", (HttpContext c, string username) => WithAccount(c, username, "guide"));
        users.MapGet("/public-key-example", (HttpContext c, string username) => WithAccount(c, username, "example"));
        foreach (var operation in new[] { "auth", "public-key", "password" })
            users.MapPut("/" + operation, (HttpContext c, string username, AccountBody body) => WithAccount(c, username, operation, body));
        foreach (var operation in new[] { "disable", "enable" })
            users.MapPut("/" + operation, (HttpContext c, string username) => WithAccount(c, username, operation));
        users.MapDelete("/public-key", (HttpContext c, string username) => WithAccount(c, username, "remove-key"));
        users.MapDelete("", (HttpContext c, string username) => WithAccount(c, username, "delete"));
        users.MapGet("/files", (HttpContext c, string username) => WithAccount(c, username, "list"));
        users.MapGet("/files/content", (HttpContext c, string username) => WithAccount(c, username, "download"));
        users.MapDelete("/files", (HttpContext c, string username) => WithAccount(c, username, "unlink"));
        users.MapPost("/files", (HttpContext c, string username) => WithAccount(c, username, "upload"));
        users.MapPut("/files/content", (HttpContext c, string username) => WithAccount(c, username, "replace"));
    }

    private static async Task<IResult> WithAccount(HttpContext c, string? name, string operation, AccountBody? body = null)
    {
        if (!ValidName(name)) return Error(400, "Use a lowercase Linux username of up to 32 characters.");
        return await SftpAccountOperations.RunAsync(name!, Execute, c.RequestAborted);

        async Task<IResult> Execute()
        {
            var store = c.RequestServices.GetRequiredService<ISftpServerStore>();
            var user = await store.GetUserAsync(name!, c.RequestAborted);
            bool owned = user != null && user.ApiClientId == Client(c);
            if (operation == "exists") return Results.Ok(new { exists = owned });
            if (operation == "create" && user != null) return Error(409, "Username unavailable.");
            if (operation != "create" && !owned) return Results.NotFound();
            switch (operation)
            {
                case "read": return Results.Ok(Summary(user!));
                case "example": return Results.Ok(new { generate = "ssh-keygen -t ed25519", publicKeyExample = SftpApiReference.ExampleKey });
                case "guide": return Guide(c, user!);
                case "list": case "download": case "unlink": case "upload": case "replace":
                    return await SftpApiFiles.ExecuteAsync(c, user!, operation);
            }
            if (!(await store.GetSettingsAsync(c.RequestAborted)).IsManagedModeEnabled)
                return Error(409, "An LMS administrator must configure SFTP managed mode first.");
            try { return await Change(c, name!, operation, body); }
            catch (InvalidOperationException) { return Error(400, "The requested account settings could not be applied. Check the authentication mode, password and public key."); }
        }
    }

    private static IResult Guide(HttpContext c, SftpManagedUser u)
    {
        var host = c.Request.Query["host"].FirstOrDefault() ?? c.Request.Host.Host;
        var path = c.Request.Query["privateKeyPath"].FirstOrDefault() ?? "~/.ssh/id_ed25519";
        if (IPAddress.TryParse(host, out var ip)) host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        else if (!Regex.IsMatch(host, @"\A[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}\z")) return Error(400, "Invalid host.");
        if (!Regex.IsMatch(path, @"\A[~/a-zA-Z0-9_][a-zA-Z0-9_./-]{0,255}\z")) return Error(400, "Invalid privateKeyPath.");
        var key = u.AuthenticationMode == SftpAuthenticationMode.PasswordOnly ? "" : $"-i {path} ";
        return Results.Ok(new { authenticationMode = u.AuthenticationMode.ToString(), command = $"sftp {key}{u.UserName}@{host}", uploadDirectory = "/files" });
    }

    private static async Task<IResult> Change(HttpContext c, string name, string operation, AccountBody? body)
    {
        var manager = c.RequestServices.GetRequiredService<ISftpServerManagerService>();
        c.RequestAborted.ThrowIfCancellationRequested();
        // Finish an approved Linux account change even if the caller disconnects.
        var ct = CancellationToken.None;
        if (body?.Password is { } pwd && (pwd.Length is 0 or > 1024 || pwd.Any(char.IsControl) || pwd != pwd.Trim()))
            return Error(400, "Use a password without control characters or surrounding whitespace.");
        if (body?.PublicKey is { } key && (key.Length > 16384 || key.Any(char.IsControl)))
            return Error(400, "Use one OpenSSH public key on a single line.");
        SftpApplyResult result;
        switch (operation)
        {
            case "delete": result = await manager.DeleteUserAsync(name, true, ct); break;
            case "disable": result = await manager.DisableUserAsync(name, true, ct); break;
            case "password":
                if (string.IsNullOrWhiteSpace(body?.Password)) return Error(400, "A password is required.");
                var password = await manager.GetPasswordResetEditorAsync(name, ct);
                password.NewPassword = body.Password;
                result = await manager.ResetPasswordAsync(password, true, ct);
                break;
            default:
                var editor = await manager.GetUserEditorAsync(operation == "create" ? null : name, ct);
                editor.ApiClientId = Client(c);
                editor.UserName = name;
                if (operation is "create" or "auth")
                {
                    if (!Mode(body?.AuthenticationMode, out var mode)) return Error(400, "Choose PasswordOnly, PublicKeyOnly or PasswordAndPublicKey.");
                    editor.AuthenticationMode = mode;
                }
                if (operation == "remove-key")
                {
                    if (editor.AuthenticationMode != SftpAuthenticationMode.PasswordOnly) return Error(409, "Switch to PasswordOnly before removing the key.");
                    editor.PublicKeys.Clear();
                }
                if (operation == "public-key" || operation == "create" && body?.PublicKey != null)
                {
                    if (string.IsNullOrWhiteSpace(body?.PublicKey)) return Error(400, "A public key is required.");
                    editor.PublicKeys = [new() { Label = "Application key", PublicKeyText = body.PublicKey }];
                }
                if (operation == "enable") editor.IsEnabled = true;
                result = await manager.SaveUserAsync(editor, operation is "auth" or "create" ? body?.Password : null, true, ct);
                break;
        }
        if (!result.Success) return Error(409, "LMS could not complete the change. The SFTP audit history contains the server result.");
        return operation == "create" ? Results.Created($"{Prefix}/users/{name}", new { username = name }) : Results.NoContent();
    }
}
