// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class XrdpSessionConfigurationService(ILinuxCommandRunner? runner = null) : ISessionConfigurationService
{
    private readonly ILinuxCommandRunner commands = runner ?? new LinuxCommandRunner(NullLogger<LinuxCommandRunner>.Instance);

    public Task<DesktopSessionConfiguration> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contents = Read("/etc/xrdp/startwm.sh");
        var remote = Read("/etc/linuxmadesane/remote-desktop").Trim();
        if (string.IsNullOrEmpty(remote)) remote = Regex.Match(contents, @"(?m)^# LMS-DEFAULT: ([^\r\n]+)").Groups[1].Value;
        if (string.IsNullOrEmpty(remote)) remote = contents.Contains("startxfce4", StringComparison.OrdinalIgnoreCase) ? "XFCE" : "System default";
        if (ReadLocalPreference() is { } preference)
            remote = DesktopSessionCatalog.Marker(preference.Profile);
        return Task.FromResult(new DesktopSessionConfiguration(remote, remote, Read("/etc/X11/default-display-manager").Trim(),
            remote == "XFCE", RdpOptimizerCatalog.SessionFiles) { PreferredUserName = ReadLocalPreference()?.UserName });
    }

    public Task<IReadOnlyList<SessionConfigurationChange>> BuildOptimizationChangesAsync(DesktopInspectionReport inspection,
        RdpOptimizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changes = new List<SessionConfigurationChange>();
        if (request.Profile is not (RdpOptimizationProfile.RdpOptimizedXfce or RdpOptimizationProfile.GnomeDesktop or RdpOptimizationProfile.KdePlasmaDesktop))
            return Task.FromResult<IReadOnlyList<SessionConfigurationChange>>(changes);
        if (request.Profile == RdpOptimizationProfile.RdpOptimizedXfce && !inspection.XfceInstalled && !request.InstallXfceIfMissing)
            return Task.FromResult<IReadOnlyList<SessionConfigurationChange>>(changes);
        var selectedUsers = request.DesktopUserNames.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var user in selectedUsers)
            if (!Regex.IsMatch(user, "^[a-z_][a-z0-9_-]*[$]?$", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Choose valid Linux accounts for the desktop.");
        if (request.ConfigureRemoteDesktop)
        {
            if (request.BindNewXrdpToLoopback && !inspection.XrdpInstalled)
                changes.Add(new("/etc/xrdp/xrdp.ini", "Bind the new desktop service to a private loopback port.", "LMS-PRIVATE-XRDP", File.Exists("/etc/xrdp/xrdp.ini"), false));
            var overrides = ReadRemoteAssignments();
            foreach (var user in selectedUsers) overrides[user] = DesktopSessionCatalog.Marker(request.Profile);
            if (selectedUsers.Length == 0) overrides.Clear();
            changes.Add(new("/etc/linuxmadesane/remote-desktop-users.json", "Set remote access desktops for the chosen users.", JsonSerializer.Serialize(overrides), true, false));
            changes.Add(new("/etc/xrdp/startwm.sh", "Use each user’s chosen remote access desktop at their next remote login.",
                BuildLauncher(request.Profile, selectedUsers.Length > 0 ? inspection.SessionConfiguration.DefaultSession : null), true, false));
            if (selectedUsers.Length == 0)
                changes.Add(new("/etc/linuxmadesane/remote-desktop", "Remember the remote access desktop for users without an individual choice.", DesktopSessionCatalog.Marker(request.Profile), true, false));
        }
        if (request.ConfigureDesktop)
        {
            var localUsers = selectedUsers.Length > 0 ? selectedUsers : [request.LocalUserName];
            var assignments = ReadLocalAssignments();
            foreach (var user in localUsers)
            {
                if (!Regex.IsMatch(user, "^[a-z_][a-z0-9_-]*[$]?$", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("Choose Linux accounts for the desktop.");
                changes.Add(new("/var/lib/AccountsService/users/" + user, "Use the selected desktop for " + user + " at their next sign-in.",
                    JsonSerializer.Serialize(new LocalPreference(user, request.Profile)), true, false));
                assignments[user] = request.Profile;
            }
            var manager = request.Profile switch
            {
                RdpOptimizationProfile.RdpOptimizedXfce => RdpOptimizerCatalog.LightdmPath,
                RdpOptimizationProfile.GnomeDesktop => RdpOptimizerCatalog.GdmPath,
                _ => RdpOptimizerCatalog.SddmPath
            };
            if (request.RepairDesktop || DesktopSessionCatalog.NeedsDisplayManager(request.Profile, inspection.SessionConfiguration.DisplayManager))
                changes.Add(new("/etc/X11/default-display-manager", "Select a compatible login manager for the next startup.", manager, true, false));
            if (request.Profile == RdpOptimizationProfile.GnomeDesktop &&
                Regex.IsMatch(Read("/etc/gdm3/custom.conf"), @"(?mi)^\s*WaylandEnable\s*=\s*false\s*$"))
                changes.Add(new("/etc/gdm3/custom.conf", "Restore GNOME's Wayland login support when required by its installed session.", "LMS-ALLOW-WAYLAND", true, false));
            changes.Add(new("/etc/linuxmadesane/local-desktop", "Remember each user’s desktop choice.",
                JsonSerializer.Serialize(new LocalPreference(localUsers[0], request.Profile, assignments)), true, false));
        }
        return request.RepairDesktop ? AddGraphicsRepairAsync(changes, request, cancellationToken) : Task.FromResult<IReadOnlyList<SessionConfigurationChange>>(changes);
    }

    private async Task<IReadOnlyList<SessionConfigurationChange>> AddGraphicsRepairAsync(List<SessionConfigurationChange> changes, RdpOptimizationRequest request, CancellationToken token)
    {
        if (request.Profile == RdpOptimizationProfile.GnomeDesktop && OperatingSystem.IsLinux())
        {
            var evidence = await commands.RunAsync(new("journalctl", ["-k", "-b", "--no-pager", "--grep", "Cursor dimensions .* exceed device max 0", "-n", "20"],
                true, TimeSpan.FromSeconds(15), "Check the GNOME display failure"), false, token);
            if (evidence.StandardOutput.Contains("exceed device max 0", StringComparison.Ordinal))
                changes.Add(new("/etc/environment", "Use software cursors for the detected graphics-driver cursor failure.", "LMS-SOFTWARE-CURSOR", true, false));
        }
        return changes;
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ApplyOptimizationChangesAsync(IReadOnlyList<SessionConfigurationChange> changes,
        bool disableGnomeAutostarts, bool dryRun, CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        foreach (var change in changes)
        {
            if (dryRun) { logs.Add(Log("Would update " + change.FilePath)); continue; }
            try
            {
                if (change.FilePath == "/etc/xrdp/startwm.sh")
                {
                    var profile = Enum.Parse<RdpOptimizationProfile>(Regex.Match(change.ContentPreview, "# LMS-PROFILE: (\\w+)").Groups[1].Value);
                    if (DesktopSessionCatalog.Find(profile, remote: true) is null)
                        throw new InvalidOperationException("The selected desktop has no installed X11 session. Choose XFCE for XRDP, or use this desktop for local login only.");
                    await CheckRdpListenerAsync(cancellationToken);
                }
                if (change.FilePath.StartsWith("/var/lib/AccountsService/users/", StringComparison.Ordinal))
                {
                    var preference = JsonSerializer.Deserialize<LocalPreference>(change.ContentPreview)!;
                    var session = DesktopSessionCatalog.Find(preference.Profile, remote: false)
                        ?? throw new InvalidOperationException("The selected desktop has no installed login session.");
                    await SetAccountSessionAsync(preference.UserName, session.Id, session.Type, cancellationToken);
                }
                else if (change.FilePath == "/etc/xrdp/xrdp.ini" && change.ContentPreview == "LMS-PRIVATE-XRDP")
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                    listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                    var contents = Read(change.FilePath);
                    var replacement = "port=tcp://127.0.0.1:" + port;
                    var match = Regex.Match(contents, @"(?m)^port=[^\r\n]*");
                    if (!match.Success) throw new InvalidOperationException("The new XRDP package did not provide a listener configuration.");
                    await WriteAsync(change.FilePath, contents[..match.Index] + replacement + contents[(match.Index + match.Length)..], cancellationToken, true);
                }
                else if (change.ContentPreview == "LMS-ALLOW-WAYLAND")
                {
                    if (DesktopSessionCatalog.Find(RdpOptimizationProfile.GnomeDesktop, false)?.Type == "wayland")
                        await WriteAsync(change.FilePath, Regex.Replace(Read(change.FilePath), @"(?mi)^\s*WaylandEnable\s*=\s*false\s*$", "WaylandEnable=true"), cancellationToken);
                }
                else if (change.ContentPreview == "LMS-SOFTWARE-CURSOR")
                {
                    var lines = Read(change.FilePath).Split('\n').Where(line => !line.StartsWith("MUTTER_DEBUG_DISABLE_HW_CURSORS=", StringComparison.Ordinal));
                    await WriteAsync(change.FilePath, string.Join('\n', lines).TrimEnd() + "\nMUTTER_DEBUG_DISABLE_HW_CURSORS=1\n", cancellationToken);
                }
                else if (change.FilePath == "/etc/linuxmadesane/local-desktop")
                {
                    var preference = JsonSerializer.Deserialize<LocalPreference>(change.ContentPreview)!;
                    var sessions = (preference.Users ?? new() { [preference.UserName] = preference.Profile })
                        .Select(pair => (pair.Key, Session: DesktopSessionCatalog.Find(pair.Value, false)))
                        .Where(pair => pair.Session is not null).ToDictionary(pair => pair.Key, pair => pair.Session!);
                    await WriteAsync(change.FilePath, JsonSerializer.Serialize(preference with { Sessions = sessions }), cancellationToken);
                }
                else await WriteAsync(change.FilePath, change.ContentPreview, cancellationToken);
                logs.Add(Log(change.Description));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logs.Add(Log("Failed to update " + change.FilePath, error.Message));
                break;
            }
        }
        return logs;
    }

    public async Task<IReadOnlyList<OperationLogEntry>> RestoreAsync(RestoreSnapshot snapshot, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        foreach (var backup in snapshot.FileBackups)
        {
            if (dryRun) { logs.Add(Log("Would restore " + backup.SourcePath)); continue; }
            try
            {
                if (backup.SourcePath.StartsWith("/var/lib/AccountsService/users/", StringComparison.Ordinal))
                {
                    var contents = backup.ExistedBeforeSnapshot ? await File.ReadAllTextAsync(backup.BackupPath, cancellationToken) : "";
                    string Field(string key) => contents.Split('\n').FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
                    await SetAccountSessionAsync(Path.GetFileName(backup.SourcePath), Field("Session") is { Length: > 0 } value ? value : Field("XSession"), Field("SessionType"), cancellationToken);
                }
                else if (backup.ExistedBeforeSnapshot)
                    await WriteAsync(backup.SourcePath, await File.ReadAllTextAsync(backup.BackupPath, cancellationToken), cancellationToken, preserve: true);
                else await RunAsync("rm", ["-f", backup.SourcePath], cancellationToken);
                logs.Add(Log("Restored " + backup.SourcePath));
            }
            catch (Exception error) when (error is not OperationCanceledException) { logs.Add(Log("Failed to restore " + backup.SourcePath, error.Message)); }
        }
        return logs;
    }

    private async Task SetAccountSessionAsync(string user, string session, string type, CancellationToken token)
    {
        var result = await RunAsync("busctl", ["call", "org.freedesktop.Accounts", "/org/freedesktop/Accounts", "org.freedesktop.Accounts", "FindUserByName", "s", user], token);
        var path = Regex.Match(result.StandardOutput, "\"(/org/freedesktop/Accounts/User[0-9]+)\"").Groups[1].Value;
        if (path.Length == 0) throw new InvalidOperationException("The local login manager could not find this Linux account.");
        await RunAsync("busctl", ["call", "org.freedesktop.Accounts", path, "org.freedesktop.Accounts.User", "SetSession", "s", session], token);
        await RunAsync("busctl", ["call", "org.freedesktop.Accounts", path, "org.freedesktop.Accounts.User", "SetSessionType", "s", type], token);
        await RunAsync("busctl", ["call", "org.freedesktop.Accounts", path, "org.freedesktop.Accounts.User", "SetXSession", "s", session], token);
    }

    private async Task CheckRdpListenerAsync(CancellationToken token)
    {
        // An active XRDP listener is retained. A conflicting RDP service is reported, never silently stopped.
        var port = XrdpConfiguration.Port(Read("/etc/xrdp/xrdp.ini"));
        var result = await RunAsync("ss", ["-H", "-ltnp", "sport", "=", ":" + port], token);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput) && !result.StandardOutput.Contains("\"xrdp\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The configured RDP port is already used by another service. Choose its intended RDP service or port before enabling XRDP; the existing service was preserved.");
    }

    private async Task WriteAsync(string path, string content, CancellationToken token, bool preserve = false)
    {
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, preserve ? content : content.TrimEnd() + "\n", token);
            await RunAsync("install", ["-D", "-m", path.EndsWith("startwm.sh", StringComparison.Ordinal) ? "755" : "644", temp, path], token);
        }
        finally { File.Delete(temp); }
    }

    private async Task<LinuxCommandResult> RunAsync(string executable, string[] args, CancellationToken token)
    {
        var result = await commands.RunAsync(new(executable, args, true, TimeSpan.FromSeconds(30), "Configure desktop session"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        return result;
    }
    private static OperationLogEntry Log(string text, string? error = null) => new(DateTimeOffset.UtcNow,
        error is null ? OperationLogLevel.Success : OperationLogLevel.Error, text, null, error is null ? 0 : -1, null, error);
    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
    private sealed record LocalPreference(string UserName, RdpOptimizationProfile Profile, Dictionary<string, RdpOptimizationProfile>? Users = null,
        Dictionary<string, DesktopSessionEntry>? Sessions = null);
    private static LocalPreference? ReadLocalPreference()
    {
        try
        {
            var preference = JsonSerializer.Deserialize<LocalPreference>(Read("/etc/linuxmadesane/local-desktop"));
            return string.IsNullOrWhiteSpace(preference?.UserName) ? null : preference;
        }
        catch (JsonException) { return null; }
    }
    private static Dictionary<string, string> ReadRemoteAssignments()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(Read("/etc/linuxmadesane/remote-desktop-users.json")) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private static Dictionary<string, RdpOptimizationProfile> ReadLocalAssignments()
    {
        var saved = ReadLocalPreference();
        return saved?.Users ?? (saved is null ? new() : new() { [saved.UserName] = saved.Profile });
    }

    internal static string BuildLauncher(RdpOptimizationProfile profile, string? existingDefault = null)
    {
        var names = profile switch
        {
            RdpOptimizationProfile.RdpOptimizedXfce => "xfce",
            RdpOptimizationProfile.GnomeDesktop => "ubuntu gnome gnome-xorg",
            _ => "plasmax11 plasma"
        };
        if (existingDefault is not null)
            names = existingDefault switch { "XFCE" => "xfce", "GNOME" => "ubuntu gnome gnome-xorg", "KDE Plasma" => "plasmax11 plasma", _ => "" };
        return $$"""
            #!/bin/sh
            # Managed by Linux Made Sane. New remote sessions only; local sessions are independent.
            # LMS-PROFILE: {{profile}}
            # LMS-DEFAULT: {{existingDefault ?? DesktopSessionCatalog.Marker(profile)}}
            unset SESSION_MANAGER DBUS_SESSION_BUS_ADDRESS
            export XDG_SESSION_TYPE=x11
            names='{{names}}'
            choice=$(python3 -c 'import json,pathlib,sys; p=pathlib.Path("/etc/linuxmadesane/remote-desktop-users.json"); print(json.loads(p.read_text()).get(sys.argv[1], "") if p.exists() else "")' "$(id -un)") || exit 1
            case "$choice" in
                XFCE) names='xfce' ;;
                GNOME) names='ubuntu gnome gnome-xorg' ;;
                'KDE Plasma') names='plasmax11 plasma' ;;
                '') ;;
                *) echo 'LMS: invalid user desktop choice.' >&2; exit 1 ;;
            esac
            if [ -z "$names" ]; then exec /etc/X11/Xsession; fi
            for session in $names; do
                entry="/usr/share/xsessions/$session.desktop"
                [ -r "$entry" ] || continue
                command=$(sed -n 's/^Exec=//p' "$entry" | head -n 1)
                [ -n "$command" ] || continue
                export DESKTOP_SESSION="$session" XDG_SESSION_DESKTOP="$session"
                exec /bin/sh -c "exec $command"
            done
            echo 'LMS: the selected desktop has no usable X11 session.' >&2
            exit 1
            """;
    }
}
