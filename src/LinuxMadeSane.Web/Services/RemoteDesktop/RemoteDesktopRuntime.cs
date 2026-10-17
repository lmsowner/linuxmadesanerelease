// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Infrastructure.Services.RemoteDesktop;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;
public sealed record RemoteDesktopEngineStatus(bool Installed, bool Healthy, string Message, bool Stopped = false);

public sealed class RemoteDesktopRuntime(IServiceScopeFactory scopes, RemoteDesktopStorageSettings paths,
    RemoteDesktopBridge bridge, RemoteDesktopSessionRegistry sessions, IWebHostEnvironment environment,
    IHttpClientFactory clients, IConfiguration configuration, ILogger<RemoteDesktopRuntime> logger)
{
    public const string Version = "1.6.0";
    private string NetworkName
    {
        get
        {
            var name = configuration["RemoteDesktop:DeploymentName"] ?? "lms-remote-desktop";
            return System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z0-9][a-z0-9-]{2,50}$") ? name : throw new InvalidOperationException("Invalid remote desktop deployment name.");
        }
    }
    private string WebName => NetworkName + "-web";
    private string DaemonName => NetworkName + "-guacd";
    private readonly SemaphoreSlim operations = new(1,1);
    private string Manifest => Path.Combine(paths.ConfigurationDirectory, "engine.json");
    public Uri EngineUri => new($"http://127.0.0.1:{Math.Clamp(configuration.GetValue("RemoteDesktop:WebPort", 4823), 1024, 65535)}/guacamole/");
    public async Task<RemoteDesktopEngineStatus> StatusAsync(CancellationToken token = default)
    {
        if (!File.Exists(Manifest)) return new(false, false, "Not installed");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var scope = scopes.CreateScope();
            var daemon = await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(new("docker",["inspect","--format","{{.State.Running}} {{index .Config.Labels \"lms.managed\"}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}",DaemonName],true,TimeSpan.FromSeconds(5),"Check remote desktop protocol service") { IsOptionalExternalTool = true },false,timeout.Token);
            if (daemon.ExitCode != 0) return new(true,false,"Protocol service could not be checked. Check Docker access and Logs.");
            var daemonState=daemon.StandardOutput.Trim();
            if (daemonState.StartsWith("false remote-desktop ",StringComparison.Ordinal)) return new(true,false,"Protocol service stopped",true);
            if (daemonState != "true remote-desktop healthy") return new(true,false,"Protocol service running but not ready. Check Health and Logs.");
            var web = await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(new("docker",["inspect","--format","{{.State.Running}} {{index .Config.Labels \"lms.managed\"}}",WebName],true,TimeSpan.FromSeconds(5),"Check remote desktop web service") { IsOptionalExternalTool = true },false,timeout.Token);
            if (web.ExitCode != 0) return new(true,false,"Web service could not be checked. Check Docker access and Logs.");
            if (web.StandardOutput.Trim() == "false remote-desktop") return new(true,false,"Web service stopped",true);
            if (web.StandardOutput.Trim() != "true remote-desktop") return new(true,false,"Web service could not be verified. Check Logs.");
            using var response = await clients.CreateClient("LmsRemoteDesktop").GetAsync(new Uri(EngineUri, "api/languages"), timeout.Token);
            return new(true, response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Ready" : "Web service running but not ready. Check Logs.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            token.ThrowIfCancellationRequested();
            logger.LogDebug(ex,"Remote desktop health check could not complete.");
            return new(true,false,ex is OperationCanceledException ? "Health check timed out; engine state is unknown. Retry Health." : "Health check unavailable; engine state is unknown. Check Logs.");
        }
    }
    public async Task OperateAsync(string action, bool approved, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!new[] { "install", "start", "stop", "restart", "upgrade", "uninstall" }.Contains(action)) throw new ArgumentException("Unknown engine action.");
        if (!approved) throw new InvalidOperationException("Review and approve the remote desktop engine installation/action first.");
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Run the remote desktop engine on the Linux LMS host.");
        await operations.WaitAsync(token);
        try
        {
            using var scope = scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>();
            async Task<string> Docker(string[] args, bool optional = false, byte[]? input = null)
            {
                var result = await runner.RunAsync(new("docker", args, true, TimeSpan.FromMinutes(10), "LMS remote desktop engine: " + args[0])
                    { IsOptionalExternalTool = optional, StandardInputBytes = input }, false, token);
                if (result.ExitCode != 0 && !optional) throw new InvalidOperationException("The remote desktop engine action failed. Check Docker access and engine logs; retry to recover.");
                return result.StandardOutput;
            }
            if (action != "install") await Docker(["version","--format","{{.Server.Version}}"]);
            var existingContainers = new List<string>();
            foreach (var name in new[] { WebName, DaemonName })
            {
                var existing = (await Docker(["inspect", "--format", "{{.Id}}", name], true)).Trim();
                if (existing.Length > 0) existingContainers.Add(name);
                var label = (await Docker(["inspect", "--format", "{{index .Config.Labels \"lms.managed\"}}", name], true)).Trim();
                if (existing.Length > 0 && label != "remote-desktop") throw new InvalidOperationException("A container with an LMS remote desktop engine name already exists and is not managed by this feature.");
            }
            var networkId = (await Docker(["network","inspect","--format","{{.Id}}",NetworkName],true)).Trim();
            var networkOwner = (await Docker(["network","inspect","--format","{{index .Labels \"lms.managed\"}}",NetworkName],true)).Trim();
            if (networkId.Length > 0 && networkOwner != "remote-desktop") throw new InvalidOperationException("An unmanaged network already uses the remote desktop engine name.");
            if (action is "stop" or "uninstall" or "restart" or "upgrade")
                foreach (var session in sessions.Active) sessions.Revoke(session);
            if (action == "uninstall")
            {
                foreach(var name in existingContainers) await Docker(["rm","-f",name]);
                if(networkId.Length > 0)
                {
                    var remaining = (await Docker(["network","inspect","--format","{{len .Containers}}",NetworkName])).Trim();
                    if(remaining == "0") await Docker(["network", "rm", NetworkName]);
                    else progress?.Report("The engine network is retained because other containers still use it.");
                }
                if (File.Exists(Manifest)) File.Delete(Manifest);
                progress?.Report("Engine removed. LMS connection metadata and credential references are preserved."); return;
            }
            if (action == "stop") { foreach(var name in existingContainers) await Docker(["stop", name]); progress?.Report("Remote desktop engine stopped."); return; }
            if (action == "install")
            {
                var probe = await runner.RunAsync(new("docker", ["version", "--format", "{{.Server.Version}}"], true, TimeSpan.FromSeconds(10), "Check remote desktop Docker runtime") { IsOptionalExternalTool = true }, false, token);
                if (probe.ExitCode != 0)
                {
                    var diagnostics = scope.ServiceProvider.GetRequiredService<IInfrastructureDiagnosticsService>();
                    var status = await diagnostics.GetPackageStatusAsync("RemoteDesktop", token);
                    if (!status.Installed) { progress?.Report("Installing Docker through LMS package management…"); await diagnostics.InstallPackagesAsync("RemoteDesktop", token); }
                    var service = scope.ServiceProvider.GetRequiredService<IServiceManagementService>();
                    var logs = await service.ApplyActionsAsync([new(ServiceActionKind.Enable,"docker","LMS remote desktop engine",false,""), new(ServiceActionKind.Start,"docker","LMS remote desktop engine",false,"")], false, token);
                    if (logs.Any(l => l.Level == OperationLogLevel.Error)) throw new InvalidOperationException("Docker could not be started. Review its service status in LMS.");
                }
            }
            else if (!File.Exists(Manifest)) throw new InvalidOperationException("Approve Install first.");
            await bridge.EnsureListeningAsync(token);
            var guacHome = Path.Combine(paths.ConfigurationDirectory, "guacamole");
            Directory.CreateDirectory(Path.Combine(guacHome, "extensions"));
            var jar = Path.Combine(environment.ContentRootPath, "Resources", "RemoteDesktop", "guacamole-lms-1.6.0.jar");
            if (!File.Exists(jar)) throw new InvalidOperationException("The packaged LMS Guacamole extension is missing. Reinstall or update LMS.");
            var tomcat = Path.Combine(environment.ContentRootPath, "Resources", "RemoteDesktop", "tomcat-server.xml");
            File.Copy(tomcat, Path.Combine(guacHome,"server.xml"), true);
            File.Copy(jar, Path.Combine(guacHome, "extensions", "lms.jar"), true);
            await File.WriteAllTextAsync(Path.Combine(guacHome, "guacamole.properties"), "guacd-hostname: " + DaemonName + "\nguacd-port: 4822\napi-session-timeout: 5\n", token);
            if (action is "install" or "upgrade")
            {
                progress?.Report($"Pulling pinned Guacamole {Version} and guacd {Version}…");
                await Docker(["pull", "guacamole/guacd:" + Version]); await Docker(["pull", "guacamole/guacamole:" + Version]);
            }
            await Docker(["network", "create", "--driver", "bridge", "--label", "lms.managed=remote-desktop", NetworkName], true);
            if (action is "upgrade" or "restart") foreach(var name in existingContainers) await Docker(["rm", "-f", name]);
            var daemon = await Docker(["inspect", "--format", "{{.State.Running}}", DaemonName], true);
            if (!daemon.Contains("true",StringComparison.Ordinal))
            {
                if (daemon.Contains("false",StringComparison.Ordinal)) await Docker(["start", DaemonName]);
                else await Docker(["run", "-d", "--name", DaemonName, "--network", NetworkName,
                    "--label", "lms.managed=remote-desktop", "--label", "lms.internal=true", "--restart", "unless-stopped", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                    "--health-interval", "10s", "--health-timeout", "5s", "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m,mode=1777", "-e", "LOG_LEVEL=warning", "guacamole/guacd:" + Version]);
            }
            var web = await Docker(["inspect", "--format", "{{.State.Running}}", WebName], true);
            if (!web.Contains("true",StringComparison.Ordinal))
            {
                if (web.Contains("false",StringComparison.Ordinal)) await Docker(["start", WebName]);
                else await Docker(["run", "-d", "--name", WebName, "--network", NetworkName,
                    "--label", "lms.managed=remote-desktop", "--label", "lms.internal=true", "--label", "lms.browser-entry=/guacamole/", "--restart", "unless-stopped",
                    "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m,mode=1777",
                    "-p", $"127.0.0.1:{EngineUri.Port}:8080", "-v", guacHome + ":/etc/guacamole:ro", "-v", Path.Combine(guacHome,"server.xml") + ":/usr/local/tomcat/conf/server.xml:ro",
                    "-v", paths.RuntimeDirectory + ":/run/lms-bridge:ro", "-e", "GUACAMOLE_HOME=/etc/guacamole", "-e", "WEBAPP_CONTEXT=guacamole",
                    "-e", "GUACD_HOSTNAME=" + DaemonName, "guacamole/guacamole:" + Version]);
            }
            await Docker(["exec", "-i", WebName, "/bin/sh", "-c", "umask 077; cat > /tmp/lms-bridge-key.$$; mv -f /tmp/lms-bridge-key.$$ /tmp/lms-bridge-key"], input: Encoding.UTF8.GetBytes(bridge.ServiceKey));
            await File.WriteAllTextAsync(Manifest, JsonSerializer.Serialize(new { version = Version, browserEntry = "/guacamole/", webImage = "guacamole/guacamole:" + Version, guacdImage = "guacamole/guacd:" + Version }), token);
            progress?.Report("Waiting for the remote desktop engine…");
            for (var attempt = 0; attempt < 45; attempt++) { if ((await StatusAsync(token)).Healthy) { progress?.Report("Remote desktop engine ready."); return; } await Task.Delay(1000, token); }
            throw new InvalidOperationException("Engine installed but not ready yet. Check Logs and use Start or Restart to retry; connections are preserved.");
        }
        finally { operations.Release(); }
    }
    public async Task<string> LogsAsync(CancellationToken token = default)
    {
        using var scope = scopes.CreateScope(); var runner = scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>();
        var output = new StringBuilder();
        foreach (var container in new[] { WebName, DaemonName })
        {
            var owner = await runner.RunAsync(new("docker",["inspect","--format","{{index .Config.Labels \"lms.managed\"}}",container],true,TimeSpan.FromSeconds(5),"Check remote desktop log source") { IsOptionalExternalTool=true },false,token);
            if(owner.ExitCode != 0 || owner.StandardOutput.Trim() != "remote-desktop") continue;
            var result = await runner.RunAsync(new("docker",["logs","--tail","60",container],true,TimeSpan.FromSeconds(10),"Read remote desktop engine logs") { IsOptionalExternalTool=true },false,token);
            output.AppendLine(container).AppendLine(result.StandardOutput).AppendLine(result.StandardError);
        }
        return output.Length == 0 ? "The managed engine is not running or has been uninstalled." : output.ToString();
    }
    internal async Task BootstrapAsync(RemoteDesktopSession session, string grant, CancellationToken token)
    {
        await bridge.EnsureListeningAsync(token);
        using var scope = scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>();
        var owner = await runner.RunAsync(new("docker", ["inspect", "--format", "{{index .Config.Labels \"lms.managed\"}}", WebName],true,TimeSpan.FromSeconds(5),"Check managed remote desktop engine"),false,token);
        if (owner.ExitCode != 0 || owner.StandardOutput.Trim() != "remote-desktop") throw new InvalidOperationException("The managed remote desktop engine is unavailable.");
        var provision = await runner.RunAsync(new("docker", ["exec","-i",WebName,"/bin/sh","-c","umask 077; cat > /tmp/lms-bridge-key.$$; mv -f /tmp/lms-bridge-key.$$ /tmp/lms-bridge-key"],true,TimeSpan.FromSeconds(5),"Restore volatile remote desktop bridge key") { StandardInputBytes = Encoding.UTF8.GetBytes(bridge.ServiceKey) },false,token);
        if (provision.ExitCode != 0) throw new InvalidOperationException("The remote desktop bridge could not be prepared. Use Start to recover.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(EngineUri,"api/tokens")) { Content = new FormUrlEncodedContent(new Dictionary<string,string>()) };
        request.Headers.Add("X-LMS-Launch-Grant",grant); request.Headers.Add("X-LMS-sessionId",session.Id.ToString());
        request.Headers.Add("X-LMS-userId",session.Connection.OwnerUserId.ToString()); request.Headers.Add("X-LMS-connectionId",session.Connection.Id.ToString());
        using var response = await clients.CreateClient("LmsRemoteDesktop").SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) { var error = await response.Content.ReadAsStringAsync(timeout.Token);
            try { using var details=JsonDocument.Parse(error); var type=details.RootElement.TryGetProperty("type",out var kind)?kind.GetString():"unknown";
                var raw=details.RootElement.TryGetProperty("message",out var m)?m.GetString():"";
                var reason = new[]{"The internal LMS bridge is unavailable.","LMS denied or expired this session.","Invalid LMS launch.","Invalid LMS connection."}.Contains(raw)?raw:"Guacamole rejected authentication";
                logger.LogWarning("Guacamole bridge bootstrap rejected (HTTP {StatusCode}, {FailureType}, {Reason}).",(int)response.StatusCode,type,reason);
            } catch(JsonException) { logger.LogWarning("Guacamole bridge bootstrap returned HTTP {StatusCode}.",(int)response.StatusCode); } throw new InvalidOperationException("The remote desktop bridge could not authorise this connection. Check the target credential, engine health and LMS session, then Connect again."); }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        session.GuacamoleToken = json.RootElement.GetProperty("authToken").GetString() ?? throw new InvalidOperationException("The remote desktop engine did not create a session.");
        session.BrowserAuthJson = JsonSerializer.Serialize(new { authToken = "lms", username = json.RootElement.GetProperty("username").GetString(), dataSource = "lms", availableDataSources = new[] { "lms" } });
    }
    internal async Task RefreshAuthenticationAsync(RemoteDesktopSession session, CancellationToken token)
    {
        await session.AuthenticationGate.WaitAsync(token);
        try
        {
            if (session.Revoked.IsCancellationRequested || string.IsNullOrEmpty(session.GuacamoleToken))
                throw new HttpRequestException("Remote desktop authentication ended.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            // Validate the existing server-owned session, rather than treating an expired
            // token as a new login. Failed logins can ban the shared internal LMS address.
            using var request = new HttpRequestMessage(HttpMethod.Get,new Uri(EngineUri,"api/session/data/lms/self"));
            request.Headers.TryAddWithoutValidation("Guacamole-Token",session.GuacamoleToken);
            using var response = await clients.CreateClient("LmsRemoteDesktop").SendAsync(request,timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Remote desktop authentication ended.");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            using var expected=JsonDocument.Parse(session.BrowserAuthJson);
            if (!json.RootElement.TryGetProperty("username",out var username) || username.GetString()!=expected.RootElement.GetProperty("username").GetString())
                throw new HttpRequestException("Remote desktop authentication ended.");
        }
        finally { session.AuthenticationGate.Release(); }
    }

    internal async Task<bool> CleanupAsync(RemoteDesktopSession session, CancellationToken token)
    {
        await session.AuthenticationGate.WaitAsync(token);
        try
        {
            if (!string.IsNullOrEmpty(session.GuacamoleToken))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await clients.CreateClient("LmsRemoteDesktop").DeleteAsync(new Uri(EngineUri, "api/tokens/" + Uri.EscapeDataString(session.GuacamoleToken)),timeout.Token);
                    if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound) return false;
                    session.GuacamoleToken = ""; session.HttpTunnelToken = ""; session.BrowserAuthJson = "";
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { logger.LogDebug("Remote desktop token cleanup deferred; the engine may be stopped."); return false; }
            }
            if (session.Connection.Options.Drive)
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(new("docker",["exec",DaemonName,"rm","-rf","--","/tmp/lms-rd/"+session.Id.ToString("N")],true,TimeSpan.FromSeconds(10),"Remove temporary remote desktop drive") { IsOptionalExternalTool = true },false,token);
            }
            return true;
        }
        finally { session.AuthenticationGate.Release(); }
    }
}
