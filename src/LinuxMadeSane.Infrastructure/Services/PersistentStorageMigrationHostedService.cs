// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed record PersistentStorageMigrationSettings(string DataDirectory, string ContentRoot, string ConnectionString);

// Also covers updates performed by the public installer, which does not use install-ce.sh.
internal sealed class PersistentStorageMigrationHostedService(
    IServiceScopeFactory scopes, PersistentStorageMigrationSettings settings,
    ILogger<PersistentStorageMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken token)
    {
        var script = Path.Combine(settings.ContentRoot, "tools", "migrate-persistent-storage.py");
        if (!OperatingSystem.IsLinux() || !File.Exists(script)) return;
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(
                new LinuxCommandRequest("python3", [script, "--previous-release", settings.ContentRoot,
                    "--data-root", settings.DataDirectory, "--connection-string", settings.ConnectionString],
                    true, TimeSpan.FromMinutes(5), "Preserve LMS state outside versioned application releases"),
                dryRun: false, token);
            if (result.ExitCode != 0)
                logger.LogError("Persistent storage migration failed: {Detail}", result.StandardError);
            else logger.LogInformation("{MigrationResult}", result.StandardOutput.Trim());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { logger.LogError(error, "Persistent storage migration requires attention; legacy files have been retained."); }
    }
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
