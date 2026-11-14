// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Web.Services;

public sealed record DesktopShareDescriptor(string SessionId, string User, string DesktopType, string Protocol);
public sealed record DesktopShareLease(string LeaseId, string LeaseToken, string Protocol, string Username, string Password, int Port, string SessionId)
{
    public override string ToString() => $"Desktop sharing lease {LeaseId} (credentials redacted)";
}
public sealed record DesktopShareRequest(string Token, string Action, string? SessionId = null, string? LeaseId = null, string? LeaseToken = null);
public interface IDesktopShareHost
{
    Task<DesktopShareDescriptor> InspectAsync(CancellationToken token = default);
    Task<DesktopShareLease> StartAsync(string sessionId, CancellationToken token = default);
    Task<bool> RenewAsync(DesktopShareLease lease, CancellationToken token = default);
    Task StopAsync(DesktopShareLease lease, CancellationToken token = default);
}
public sealed class DesktopShareHost(IServiceScopeFactory scopes, IWebHostEnvironment environment, IConfiguration configuration) : IDesktopShareHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal async Task<T> ExecuteAsync<T>(string action, object input, CancellationToken token)
    {
        var helper = configuration["DesktopSharing:HelperPath"] ?? Path.Combine(environment.ContentRootPath, "Resources", "DesktopSharing", "desktop_share.py");
        if (!File.Exists(helper)) throw new InvalidOperationException("This LMS installation is missing its desktop-sharing files. Install a complete CE package, then retry.");
        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(new("python3", [helper, action], true,
            action is "start" or "inspect" or "repair" ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30), "Manage existing desktop sharing")
            { StandardInputBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, Json)) }, false, token);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        if (result.ExitCode != 0) throw new InvalidOperationException("The existing desktop could not be shared.");
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidOperationException("The host returned no desktop sharing result.");
    }
    public Task<DesktopShareDescriptor> InspectAsync(CancellationToken token = default) => ExecuteAsync<DesktopShareDescriptor>("inspect", new { }, token);
    public Task<DesktopShareLease> StartAsync(string sessionId, CancellationToken token = default) => ExecuteAsync<DesktopShareLease>("start", new { sessionId }, token);
    public async Task<bool> RenewAsync(DesktopShareLease lease, CancellationToken token = default) =>
        (await ExecuteAsync<JsonElement>("renew", new { lease.LeaseId, lease.LeaseToken }, token)).GetProperty("active").GetBoolean();
    public async Task StopAsync(DesktopShareLease lease, CancellationToken token = default) =>
        _ = await ExecuteAsync<JsonElement>("stop", new { lease.LeaseId, lease.LeaseToken }, token);
}
