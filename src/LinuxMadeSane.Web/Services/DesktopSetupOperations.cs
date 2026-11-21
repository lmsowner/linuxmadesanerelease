// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.RdpOptimizer;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Web.Services;

public sealed record DesktopSetupOperation(bool Running, string Message, RdpOptimizationResult? Result = null);

// The operation owns its service scope and survives closing or refreshing the page.
public sealed class DesktopSetupOperations(IServiceScopeFactory scopes, ILogger<DesktopSetupOperations> logger)
{
    private readonly object gate = new();
    private DesktopSetupOperation state = new(false, "");
    public DesktopSetupOperation Current { get { lock (gate) return state; } }
    public void Start(RdpOptimizationRequestEditor editor)
    {
        var request = JsonSerializer.Deserialize<RdpOptimizationRequestEditor>(JsonSerializer.Serialize(editor))!;
        lock (gate)
        {
            if (state.Running) throw new InvalidOperationException("A desktop operation is already running on this host.");
            state = new(true, request.DryRun ? "Checking the proposed changes…" : $"{(request.RepairDesktop ? "Repairing" : !request.ConfigureRemoteDesktop && !request.ConfigureDesktop ? "Installing" : "Setting up")} {DesktopSessionCatalog.Marker(request.Profile)}{(request.DesktopUserNames.Count > 0 ? " for " + string.Join(", ", request.DesktopUserNames) : "")}. This can take several minutes. You can return to Desktop Setup to check progress.");
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IRdpOptimizationService>().ExecuteAsync(request);
                var integrationMessage = "";
                if (result.Success && !request.DryRun && request.DesktopUserNames.Count > 0 && (request.ConfigureDesktop || request.ConfigureRemoteDesktop) &&
                    scope.ServiceProvider.GetService<DesktopHelperSetupService>() is { } helper)
                {
                    var integration = await helper.SetEnabledAsync(true);
                    if (!integration.Succeeded) integrationMessage = " Assistant integration could not start: " + integration.Message;
                }
                lock (gate) state = new(false, (result.Success
                    ? request.DryRun ? "Preview complete. No changes applied."
                        : request.Profile == LinuxMadeSane.Core.Enums.RdpOptimizationProfile.RestoreFullDesktop ? "Previous desktop configuration restored. Installed packages retained."
                        : request.RepairDesktop ? $"{DesktopSessionCatalog.Marker(request.Profile)} package files and login settings repaired. User files and other desktops were kept. Save your work and restart this machine to start the repaired desktop. A restore snapshot was saved before the repair."
                        : !request.ConfigureDesktop ? "Desktop packages installed."
                        : $"{DesktopSessionCatalog.Marker(request.Profile)} selected for {string.Join(", ", request.DesktopUserNames)}. " +
                            (result.Plan.SessionChanges.Any(change => change.FilePath == "/etc/X11/default-display-manager")
                                ? "Save your work and restart this machine to use the selected desktop. Its login manager needs to change at startup."
                                : "Save your work, then sign out and sign in to use it. Your current session stays open.")
                    : "Setup needs attention. Review the operation log below; any attempted restoration is recorded there.") + integrationMessage, result);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Desktop setup failed");
                lock (gate) state = new(false, error.Message);
            }
        });
    }
}
