// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services.RemoteDesktop;
public sealed class RemoteDesktopSessionMonitor(IServiceScopeFactory scopes, RemoteDesktopSessionRegistry sessions,
    RemoteDesktopRuntime runtime, RemoteDesktopBridge bridge, LinuxMadeSane.Infrastructure.Services.RemoteDesktop.RemoteDesktopStorageSettings paths,
    ILogger<RemoteDesktopSessionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (File.Exists(Path.Combine(paths.ConfigurationDirectory,"engine.json")))
        {
            try { await bridge.EnsureListeningAsync(stoppingToken); }
            catch (Exception) { logger.LogWarning("The remote desktop bridge could not start; use Start to recover the engine."); }
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                foreach (var session in sessions.Active)
                {
                    using var scope = scopes.CreateScope();
                    try { if (!string.IsNullOrEmpty(session.HttpTunnelToken) && DateTimeOffset.UtcNow-session.LastActivity > TimeSpan.FromMinutes(5) || !await scope.ServiceProvider.GetRequiredService<RemoteDesktopAuthorizer>().ValidAsync(session,stoppingToken)) sessions.Revoke(session); }
                    catch (Exception) { sessions.Revoke(session); }
                }
                var count = sessions.PendingCleanup.Count;
                for (var i = 0; i < count && sessions.PendingCleanup.TryDequeue(out var ended); i++)
                {
                    try { if (!await runtime.CleanupAsync(ended,stoppingToken)) sessions.PendingCleanup.Enqueue(ended); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception) { sessions.PendingCleanup.Enqueue(ended); logger.LogWarning("Remote desktop cleanup will retry."); }
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { foreach (var session in sessions.Active) { sessions.Revoke(session); try { await runtime.CleanupAsync(session,CancellationToken.None); } catch (Exception) { logger.LogWarning("Remote desktop engine cleanup was interrupted during shutdown."); } } }
    }
}
