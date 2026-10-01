// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed record LmsHostEdgeGatewayLink(string Label, string Url);
public sealed record LmsHostEdgeGatewaySummary(int RouteCount, IReadOnlyList<LmsHostEdgeGatewayLink> Links)
{
    public static LmsHostEdgeGatewaySummary Empty { get; } = new(0, []);
}

public sealed class LmsHostEdgeGatewaySummaryService(
    IServiceScopeFactory scopeFactory,
    ICommandExecutionService commandExecutionService,
    ILogger<LmsHostEdgeGatewaySummaryService> logger)
{
    // Read only published route names and paths. No credentials or other configuration leave the host.
    internal const string RemoteProbeCommand = """
        if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then lms_probe_runner='sudo -n python3'; else lms_probe_runner='python3'; fi
        $lms_probe_runner - <<'LMS_EDGE_ROUTES'
        import json, os, sqlite3, subprocess, urllib.parse
        candidates = []
        env_files = ['/etc/linuxmadesane/ce/service.env', '/etc/linuxmadesane/pro/service.env']
        try:
            configured = subprocess.check_output(['systemctl', 'show', 'linux-made-sane.service', '-p', 'EnvironmentFiles', '--value'], stderr=subprocess.DEVNULL, text=True).strip().split()
            if configured and configured[0].startswith('/'):
                env_files.insert(0, configured[0])
        except Exception:
            pass
        for env_file in env_files:
            try:
                for line in open(env_file):
                    if line.startswith('ConnectionStrings__LinuxMadeSane='):
                        connection = line.split('=', 1)[1].strip().strip('"')
                        for part in connection.split(';'):
                            key, _, value = part.partition('=')
                            if key.strip().lower() in ('data source', 'datasource', 'filename'):
                                candidates.append(value.strip().strip('"'))
            except OSError:
                pass
        candidates.extend(['/var/lib/linuxmadesane/ce/linuxmadesane.db', '/var/lib/linuxmadesane/pro/linuxmadesane.db'])
        for path in dict.fromkeys(candidates):
            if not os.path.isfile(path):
                continue
            try:
                with sqlite3.connect('file:' + urllib.parse.quote(os.path.abspath(path)) + '?mode=ro', uri=True, timeout=2) as db:
                    count = db.execute('SELECT COUNT(*) FROM edge_gateway_routes WHERE Enabled = 1').fetchone()[0]
                    routes = db.execute('SELECT Hostname, TargetPathPrefix FROM edge_gateway_routes WHERE Enabled = 1 ORDER BY Hostname COLLATE NOCASE, TargetPathPrefix LIMIT 5').fetchall()
                    print(json.dumps({'routeCount': count, 'routes': [{'hostname': host, 'path': prefix} for host, prefix in routes]}))
                    break
            except sqlite3.Error:
                continue
        else:
            print(json.dumps({'routeCount': 0, 'routes': []}))
        LMS_EDGE_ROUTES
        """;

    public async Task<LmsHostEdgeGatewaySummary> GetAsync(ManagedHost host, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (AiLocalMachine.IsLocalMachine(host.Id))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var routes = (await scope.ServiceProvider.GetRequiredService<IEdgeGatewayService>().ListRoutesAsync(timeout.Token))
                    .Where(route => route.Enabled).OrderBy(route => route.Hostname, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(route => route.TargetPathPrefix, StringComparer.Ordinal).ToArray();
                return BuildSummary(routes.Length, routes.Take(5).Select(route => (route.Hostname, route.TargetPathPrefix)));
            }

            var result = await commandExecutionService.ExecuteAsync(host, RemoteProbeCommand, cancellationToken: timeout.Token);
            return result.IsSuccess ? ParseSummary(result.StandardOutput) : LmsHostEdgeGatewaySummary.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read Edge Gateway routes from LMS host {HostId}.", host.Id);
            return LmsHostEdgeGatewaySummary.Empty;
        }
    }

    internal static LmsHostEdgeGatewaySummary ParseSummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return BuildSummary(root.GetProperty("routeCount").GetInt32(), root.GetProperty("routes").EnumerateArray()
            .Select(route => (route.GetProperty("hostname").GetString() ?? string.Empty, route.GetProperty("path").GetString() ?? "/")));
    }

    private static LmsHostEdgeGatewaySummary BuildSummary(int count, IEnumerable<(string Hostname, string Path)> routes)
    {
        var links = routes.Where(route => Uri.CheckHostName(route.Hostname) == UriHostNameType.Dns)
            .Select(route =>
            {
                var path = string.IsNullOrWhiteSpace(route.Path) ? "/" : route.Path;
                var uri = new UriBuilder(Uri.UriSchemeHttps, route.Hostname) { Path = path }.Uri;
                return new LmsHostEdgeGatewayLink(route.Hostname + (path == "/" ? "" : path), uri.AbsoluteUri);
            }).DistinctBy(link => link.Url, StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        return new LmsHostEdgeGatewaySummary(Math.Max(count, links.Length), links);
    }
}
