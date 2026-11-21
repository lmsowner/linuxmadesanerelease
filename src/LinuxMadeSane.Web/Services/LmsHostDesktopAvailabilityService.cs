// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;
namespace LinuxMadeSane.Web.Services;

public sealed class LmsHostDesktopAvailabilityService(IServiceScopeFactory scopes, ILogger<LmsHostDesktopAvailabilityService> logger)
{
    // Read the target itself, including older LMS installations with an incorrect HasDesktop flag.
    // This never installs packages, starts a session, changes settings or creates an HTTP relay.
    internal const string ProbeCommand = """
        if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then runner='sudo -n python3'; else runner='python3'; fi
        $runner - <<'LMS_DESKTOP_AVAILABILITY'
        import json, pathlib, pwd, shlex, shutil, subprocess
        def run(args):
            try:
                return subprocess.check_output(args, text=True, stderr=subprocess.DEVNULL, timeout=5).strip()
            except (OSError, subprocess.SubprocessError):
                return None
        def read_json(path):
            try:
                value = json.loads(pathlib.Path(path).read_text())
                return value if isinstance(value, dict) else {}
            except (OSError, ValueError):
                return {}
        def regular_user(name):
            try:
                account = pwd.getpwnam(name)
                return 1000 <= account.pw_uid < 65534 and not account.pw_shell.endswith(('nologin', 'false'))
            except (KeyError, TypeError):
                return False
        def configured_desktop():
            choice = read_json('/etc/linuxmadesane/local-desktop')
            legacy = read_json('/etc/linuxmadesane/remote-desktop-users.json')
            name = choice.get('UserName') or (next(iter(legacy)) if len(legacy) == 1 else None)
            if not regular_user(name): return False
            session = choice.get('Sessions', {}).get(name, {})
            if not session:
                profile = choice.get('Users', {}).get(name, choice.get('Profile') if choice.get('UserName') == name else None)
                if profile == 1 or profile is None and legacy.get(name) == 'XFCE':
                    try:
                        values = dict(line.split('=', 1) for line in pathlib.Path('/usr/share/xsessions/xfce.desktop').read_text().splitlines() if line.startswith(('Exec=', 'Hidden=')))
                        if values.get('Hidden') != 'true': session = dict(Type='x11', Command=values.get('Exec', ''))
                    except OSError: pass
            # The sharing helper can start an explicitly selected X11 desktop. Wayland needs a running user session.
            if session.get('Type') != 'x11': return False
            try: command = shlex.split(session.get('Command', ''))
            except ValueError: return False
            return bool(command and shutil.which(command[0]))
        def available():
            sessions = run(['loginctl', 'list-sessions', '--no-legend', '--no-pager'])
            if sessions is None: return False
            for line in sessions.splitlines():
                if not line.split(): continue
                data = run(['loginctl', 'show-session', line.split()[0], '-p', 'Name', '-p', 'Type', '-p', 'Class', '-p', 'State'])
                fields = dict(item.split('=', 1) for item in (data or '').splitlines() if '=' in item)
                if fields.get('Type') in ('x11', 'wayland') and fields.get('Class') in ('user', 'user-early') and fields.get('State') in ('active', 'online') and regular_user(fields.get('Name')):
                    return True
            root = pathlib.Path('/run/linuxmadesane-desktops')
            try:
                for directory in root.iterdir() if root.is_dir() else []:
                    target = read_json(directory / 'target.json')
                    uid = target.get('User', '')
                    if isinstance(uid, str) and uid.isdigit() and 1000 <= int(uid) < 65534 and target.get('Type') == 'x11' and regular_user(target.get('UserName')) and run(['systemctl', 'is-active', 'lms-desktop-session-' + uid]) == 'active':
                        return True
            except OSError: pass
            return configured_desktop()
        if __name__ == '__main__':
            try: result = available()
            except Exception: result = False
            print(json.dumps({'available': result}))
        LMS_DESKTOP_AVAILABILITY
        """;

    public Task<bool> GetAsync(ManagedHost host, CancellationToken token = default) => Task.Run(async () =>
    {
        if (!ManagedHostCapabilities.IsLmsHost(host)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await using var scope = scopes.CreateAsyncScope();
            if (AiLocalMachine.IsLocalMachine(host.Id))
            {
                var result = await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(
                    new LinuxCommandRequest("/bin/sh", ["-c", ProbeCommand], true, TimeSpan.FromSeconds(20), "Check available graphical desktop"), false, timeout.Token);
                return result.ExitCode == 0 && Parse(result.StandardOutput);
            }
            var remote = await scope.ServiceProvider.GetRequiredService<ICommandExecutionService>().ExecuteAsync(host, ProbeCommand, cancellationToken: timeout.Token);
            return remote.IsSuccess && Parse(remote.StandardOutput);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { logger.LogDebug(error, "Desktop availability could not be verified for {HostId}", host.Id); return false; }
    }, token);

    internal static bool Parse(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.TryGetProperty("available", out var value) && value.ValueKind == JsonValueKind.True;
    }
}
