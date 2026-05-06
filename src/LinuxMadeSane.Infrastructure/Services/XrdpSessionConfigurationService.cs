using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class XrdpSessionConfigurationService : ISessionConfigurationService
{
    public Task<DesktopSessionConfiguration> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var xrdpPath = "/etc/xrdp/startwm.sh";
        var skelPath = "/etc/skel/.xsession";
        var displayManagerPath = "/etc/X11/default-display-manager";

        var xrdpContents = SafeReadAllText(xrdpPath);
        var skelContents = SafeReadAllText(skelPath);
        var displayManager = SafeReadAllText(displayManagerPath)?.Trim();
        var xrdpUsesXfce = ContainsXfce(xrdpContents) || ContainsXfce(skelContents);
        var defaultSession = xrdpUsesXfce ? "xfce" : "system-default";

        return Task.FromResult(new DesktopSessionConfiguration(
            defaultSession,
            xrdpUsesXfce ? "startxfce4" : "default-session",
            displayManager,
            xrdpUsesXfce,
            RdpOptimizerCatalog.SessionFiles));
    }

    public Task<IReadOnlyList<SessionConfigurationChange>> BuildOptimizationChangesAsync(
        DesktopInspectionReport inspection,
        RdpOptimizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changes = new List<SessionConfigurationChange>();

        switch (request.Profile)
        {
            case RdpOptimizationProfile.RdpOptimizedXfce:
            {
                var willHaveXfce = inspection.XfceInstalled || request.InstallXfceIfMissing;
                if (!willHaveXfce)
                {
                    break;
                }

                changes.Add(new SessionConfigurationChange(
                    "/etc/xrdp/startwm.sh",
                    "Configure XRDP to launch XFCE by default.",
                    BuildManagedXfceStartwmScript(),
                    true,
                    true));

                changes.Add(new SessionConfigurationChange(
                    "/etc/skel/.xsession",
                    "Ensure new users inherit an XFCE session for XRDP logins.",
                    "startxfce4",
                    true,
                    false));

                changes.Add(new SessionConfigurationChange(
                    "/etc/X11/default-display-manager",
                    "Set lightdm as the primary display manager.",
                    RdpOptimizerCatalog.LightdmPath,
                    true,
                    true));

                if (request.DisableGnomeAutostarts)
                {
                    foreach (var fileName in RdpOptimizerCatalog.GnomeAutostartFiles)
                    {
                        changes.Add(new SessionConfigurationChange(
                            $"/etc/xdg/autostart/{fileName}",
                            $"Disable GNOME autostart entry {fileName}.",
                            BuildDisabledAutostartFile(fileName),
                            true,
                            false));
                    }
                }

                break;
            }
            case RdpOptimizationProfile.GnomeDesktop:
                changes.Add(new SessionConfigurationChange(
                    "/etc/xrdp/startwm.sh",
                    "Reset XRDP to the system default desktop session.",
                    BuildManagedDefaultStartwmScript(),
                    true,
                    true));

                changes.Add(new SessionConfigurationChange(
                    "/etc/skel/.xsession",
                    "Ensure new users inherit the system default desktop session.",
                    "exec /etc/X11/Xsession",
                    true,
                    false));

                changes.Add(new SessionConfigurationChange(
                    "/etc/X11/default-display-manager",
                    "Set GDM as the primary display manager.",
                    RdpOptimizerCatalog.GdmPath,
                    true,
                    true));
                break;
            case RdpOptimizationProfile.KdePlasmaDesktop:
                changes.Add(new SessionConfigurationChange(
                    "/etc/xrdp/startwm.sh",
                    "Reset XRDP to the system default desktop session.",
                    BuildManagedDefaultStartwmScript(),
                    true,
                    true));

                changes.Add(new SessionConfigurationChange(
                    "/etc/skel/.xsession",
                    "Ensure new users inherit the system default desktop session.",
                    "exec /etc/X11/Xsession",
                    true,
                    false));

                changes.Add(new SessionConfigurationChange(
                    "/etc/X11/default-display-manager",
                    "Set SDDM as the primary display manager.",
                    RdpOptimizerCatalog.SddmPath,
                    true,
                    true));
                break;
        }

        return Task.FromResult<IReadOnlyList<SessionConfigurationChange>>(changes);
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ApplyOptimizationChangesAsync(
        IReadOnlyList<SessionConfigurationChange> changes,
        bool disableGnomeAutostarts,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Info,
                    $"Would update {change.FilePath}",
                    null,
                    0,
                    change.ContentPreview,
                    null));
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(change.FilePath)!);
                await File.WriteAllTextAsync(change.FilePath, change.ContentPreview.TrimEnd() + Environment.NewLine, cancellationToken);

                if (change.FilePath.EndsWith("startwm.sh", StringComparison.OrdinalIgnoreCase) &&
                    (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                {
                    File.SetUnixFileMode(
                        change.FilePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Success,
                    change.Description,
                    null,
                    0,
                    change.FilePath,
                    null));
            }
            catch (Exception exception)
            {
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Error,
                    $"Failed to update {change.FilePath}",
                    null,
                    -1,
                    null,
                    exception.Message));
            }
        }

        return logs;
    }

    public async Task<IReadOnlyList<OperationLogEntry>> RestoreAsync(
        RestoreSnapshot snapshot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        foreach (var fileBackup in snapshot.FileBackups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Info,
                    $"Would restore {fileBackup.SourcePath}",
                    null,
                    0,
                    fileBackup.BackupPath,
                    null));
                continue;
            }

            try
            {
                if (fileBackup.ExistedBeforeSnapshot)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fileBackup.SourcePath)!);
                    File.Copy(fileBackup.BackupPath, fileBackup.SourcePath, overwrite: true);
                }
                else if (File.Exists(fileBackup.SourcePath))
                {
                    File.Delete(fileBackup.SourcePath);
                }

                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Success,
                    $"Restored {fileBackup.SourcePath}",
                    null,
                    0,
                    fileBackup.BackupPath,
                    null));
            }
            catch (Exception exception)
            {
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Error,
                    $"Failed to restore {fileBackup.SourcePath}",
                    null,
                    -1,
                    null,
                    exception.Message));
            }
        }

        return logs;
    }

    private static bool ContainsXfce(string? contents) =>
        !string.IsNullOrWhiteSpace(contents) &&
        contents.Contains("startxfce4", StringComparison.OrdinalIgnoreCase);

    private static string? SafeReadAllText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static string BuildManagedXfceStartwmScript() =>
        """
        #!/bin/sh
        # Managed by Linux Made Sane Desktop Modes.
        if [ -r /etc/profile ]; then
            . /etc/profile
        fi
        if [ -r "$HOME/.profile" ]; then
            . "$HOME/.profile"
        fi
        export DESKTOP_SESSION=xfce
        export XDG_SESSION_DESKTOP=xfce
        export XDG_CURRENT_DESKTOP=XFCE
        exec startxfce4
        """;

    private static string BuildManagedDefaultStartwmScript() =>
        """
        #!/bin/sh
        # Managed by Linux Made Sane Desktop Modes.
        if [ -r /etc/profile ]; then
            . /etc/profile
        fi
        if [ -r "$HOME/.profile" ]; then
            . "$HOME/.profile"
        fi
        exec /etc/X11/Xsession
        """;

    private static string BuildDisabledAutostartFile(string fileName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Desktop Entry]");
        builder.AppendLine("Type=Application");
        builder.AppendLine($"X-LinuxMadeSane-Source={fileName}");
        builder.AppendLine("Hidden=true");
        builder.AppendLine("X-GNOME-Autostart-enabled=false");
        builder.AppendLine("X-LinuxMadeSane-Disabled=true");
        return builder.ToString();
    }
}
