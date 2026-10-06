// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed record LmsHostBenchmark(PerformanceBenchmarkResult? Result, string? Error = null);
public sealed class LmsHostBenchmarkService(IServiceScopeFactory scopes, PerformanceBenchmarkService local,
    ILogger<LmsHostBenchmarkService> logger)
{
    public const string ReadCommand = """
        if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then runner='sudo -n python3'; else runner='python3'; fi
        $runner - <<'LMS_BENCHMARK'
        import pathlib,json
        root=pathlib.Path('/var/lib/linuxmadesane/ce')
        env=pathlib.Path('/etc/linuxmadesane/ce/service.env')
        try: lines=env.read_text().splitlines()
        except OSError: lines=[]
        for line in lines:
            if line.startswith('ConnectionStrings__LinuxMadeSane='):
                for part in line.split('=',1)[1].strip().strip('"').split(';'):
                    key,_,value=part.partition('=')
                    if key.strip().lower() in ('data source','datasource','filename') and value.strip().startswith('/'):
                        root=pathlib.Path(value.strip()).parent
        p=root/'performance'/'latest.json'
        if p.exists():
            if p.stat().st_size>1048576: raise ValueError('Saved benchmark file is too large')
            result=json.loads(p.read_text())
            # The Hosts table and remote Details need measurements, not the full sysbench log.
            result['rawOutput']=''
            print(json.dumps(result,separators=(',',':')))
        else: print('null')
        LMS_BENCHMARK
        """;
    public async Task<LmsHostBenchmark> GetAsync(ManagedHost host, CancellationToken token)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id)) return new(local.GetStatus().Result);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ICommandExecutionService>()
                .ExecuteAsync(host, ReadCommand, cancellationToken: timeout.Token);
            if (!result.IsSuccess) return new(null, "Could not read this host's saved benchmark using its SSH credentials.");
            return new(PerformanceBenchmarkService.ParseSaved(result.StandardOutput));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogDebug(ex, "Could not read benchmark from {Host}.", host.Name); return new(null, "Saved benchmark unavailable: " + ex.Message); }
    }
}
