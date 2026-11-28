// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web.Services;

public sealed record HostDesktopSession(string Id, string User, string Desktop, string Type, string State, bool Remote);

public sealed class DesktopHostSessions(ILinuxCommandRunner commands)
{
    public IReadOnlyList<string> Users() => File.ReadLines("/etc/passwd").Select(line => line.Split(':'))
        .Where(p => p.Length == 7 && int.TryParse(p[2], out var uid) && uid >= 1000 && uid < 65534 &&
            !p[6].EndsWith("nologin", StringComparison.Ordinal) && !p[6].EndsWith("false", StringComparison.Ordinal))
        .Select(p => p[0]).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<IReadOnlyList<HostDesktopSession>> ListAsync(CancellationToken token = default)
    {
        var list = await Run("loginctl", ["list-sessions", "--no-legend", "--no-pager"], token);
        var sessions = new List<HostDesktopSession>();
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (id is null) continue;
            var contents = await Run("loginctl", ["show-session", id, "-p", "Id", "-p", "Name", "-p", "Desktop", "-p", "Type", "-p", "State", "-p", "Remote", "-p", "Class"], token);
            var fields = contents.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (fields.GetValueOrDefault("Type") is not ("x11" or "wayland") || fields.GetValueOrDefault("Class") is not ("user" or "user-early")) continue;
            var desktop = fields.GetValueOrDefault("Desktop", "");
            if (string.IsNullOrWhiteSpace(desktop))
            {
                var group = (await Run("systemctl", ["show", "session-" + id + ".scope", "-p", "ControlGroup", "--value"], token)).Trim();
                if (group.StartsWith("/user.slice/", StringComparison.Ordinal) && !group.Contains("..", StringComparison.Ordinal))
                {
                    var processes = await Run("bash", ["-c", "while read -r pid; do cat \"/proc/$pid/comm\" 2>/dev/null || true; done < \"$1/cgroup.procs\"", "lms-desktop", "/sys/fs/cgroup" + group], token);
                    var names = processes.Split('\n').ToHashSet(StringComparer.Ordinal);
                    desktop = names.Contains("xfce4-session") ? "XFCE" : names.Contains("gnome-shell") ? "GNOME" : names.Contains("plasmashell") ? "KDE Plasma" : "Unknown";
                }
            }
            sessions.Add(new(fields.GetValueOrDefault("Id", id), fields.GetValueOrDefault("Name", ""), desktop,
                fields["Type"], fields.GetValueOrDefault("State", ""), fields.GetValueOrDefault("Remote") == "yes"));
        }
        const string managedRoot = "/run/linuxmadesane-desktops";
        if (Directory.Exists(managedRoot))
            foreach (var file in Directory.EnumerateDirectories(managedRoot).Select(directory => Path.Combine(directory, "target.json")).Where(File.Exists))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file, token));
                    var target = document.RootElement; var uid = target.GetProperty("User").GetString();
                    if (!int.TryParse(uid, out var parsedUid) || parsedUid < 1000 || parsedUid >= 65534) continue;
                    var active = await Run("systemctl", ["show", "lms-desktop-session-" + uid, "-p", "ActiveState", "--value"], token);
                    if (active.Trim() == "active") sessions.Add(new("lms-" + uid, target.GetProperty("UserName").GetString()!,
                        target.TryGetProperty("Desktop", out var desktop) ? desktop.GetString() ?? "Desktop" : "XFCE", "x11", "active", true));
                }
                catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { }
            }
        return sessions;
    }

    public async Task EndAsync(string id)
    {
        if (!(await ListAsync()).Any(session => session.Id == id)) throw new InvalidOperationException("This desktop session has already ended.");
        if (id.StartsWith("lms-", StringComparison.Ordinal) && int.TryParse(id[4..], out var uid) && uid >= 1000 && uid < 65534)
            await Run("systemctl", ["stop", "lms-desktop-session-" + uid], CancellationToken.None);
        else await Run("loginctl", ["terminate-session", id], CancellationToken.None);
    }

    private async Task<string> Run(string tool, string[] args, CancellationToken token)
    {
        var result = await commands.RunAsync(new(tool, args, true, TimeSpan.FromSeconds(15), "Manage graphical sessions"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        return result.StandardOutput;
    }
}
