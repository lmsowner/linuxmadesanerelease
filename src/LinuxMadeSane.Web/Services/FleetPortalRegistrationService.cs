// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
namespace LinuxMadeSane.Web.Services;

public sealed record FleetPortalRegistrationResult(ManagedHost Host,string State,string Detail);

public sealed class FleetPortalRegistrationService(IServiceScopeFactory scopes)
{
    private readonly object sync=new();
    private FleetPortalRegistrationResult[] results=[];
    private bool running;
    private bool checking;
    private readonly Dictionary<Guid,RemotePortalRegistrationStatus> registrations=[];
    private readonly Dictionary<Guid,string> checkErrors=[];
    public bool IsChecking { get { lock(sync) return checking; } }
    public RemotePortalRegistrationStatus? Status(Guid hostId) { lock(sync) return registrations.GetValueOrDefault(hostId); }
    public string? CheckError(Guid hostId) { lock(sync) return checkErrors.GetValueOrDefault(hostId); }
    public bool CanRegister(Guid hostId)=>Status(hostId) is {Registered:false};
    public Task RefreshAsync(IReadOnlyList<ManagedHost> hosts)=>RefreshAsync(hosts,async(host,token)=>
    {
        using var scope=scopes.CreateScope();
        if(AiLocalMachine.IsLocalMachine(host.Id))
        {
            var status=await scope.ServiceProvider.GetRequiredService<PortalAddonBootstrapService>().StatusAsync(token);
            return new(status?.LocalInstanceId ?? Guid.Empty,status?.PortalOrganizationId,status?.PortalInstanceId,
                status is {IsEnabled:true,PortalOrganizationId:not null,PortalInstanceId:not null} && !status.LastConnectionStatus.StartsWith("Portal registration accepted. Installing connector",StringComparison.Ordinal),"Checked locally.");
        }
        // Ask the running LMS through its authenticated SSH tunnel. The SSH user
        // must not need direct access to LMS's private database or passwordless sudo.
        return await scope.ServiceProvider.GetRequiredService<RemoteLmsSshTunnelService>()
            .GetPortalRegistrationAsync(host.Id,token);
    });
    internal async Task RefreshAsync(IReadOnlyList<ManagedHost> hosts,Func<ManagedHost,CancellationToken,Task<RemotePortalRegistrationStatus>> read)
    {
        lock(sync) { if(checking || running) return; checking=true; }
        Notify();
        try
        {
            // Keep SSH probes bounded; never issue registration codes during discovery.
            using var slots=new SemaphoreSlim(2);
            await Task.WhenAll(hosts.DistinctBy(x=>x.Id).Select(async host=>
            {
                await slots.WaitAsync();
                try
                {
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var status=await read(host,timeout.Token);
                    lock(sync) { registrations[host.Id]=status; checkErrors.Remove(host.Id); }
                }
                catch(Exception error) { lock(sync) { registrations.Remove(host.Id);checkErrors[host.Id]=error is OperationCanceledException ? "Registration check timed out." : error.Message; } }
                finally { slots.Release();Notify(); }
            }));
        }
        finally { lock(sync) checking=false;Notify(); }
    }
    public event Action? Changed;
    public bool IsRunning { get { lock(sync) return running; } }
    public IReadOnlyList<FleetPortalRegistrationResult> Results { get { lock(sync) return results.ToArray(); } }
    public bool Start(IReadOnlyList<ManagedHost> hosts)
    {
        lock(sync)
        {
            if(running || checking || hosts.Count==0) return false;
            results=hosts.DistinctBy(x=>x.Id).Where(x=>registrations.GetValueOrDefault(x.Id) is {Registered:false}).Select(x=>new FleetPortalRegistrationResult(x,"Queued","Waiting to register.")).ToArray();
            if(results.Length==0) return false;
            running=true;
        }
        Notify(); _=RunAsync(); return true;
    }
    private async Task RunAsync()
    {
        try
        {
            using var scope=scopes.CreateScope();
            var registration=scope.ServiceProvider.GetRequiredService<PortalAddonBootstrapService>();
            var parent=await registration.StatusAsync();
            if(parent?.PortalOrganizationId is not {} account || !parent.IsEnabled) throw new InvalidOperationException("Register this LMS instance with Portal first.");
            var remote=scope.ServiceProvider.GetRequiredService<RemoteLmsSshTunnelService>();
            foreach(var item in Results)
            {
                Set(item.Host,"Registering","Connecting using saved SSH access; registering with the current Portal account…");
                try
                {
                    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var result=await remote.RegisterWithPortalAsync(item.Host.Id,account,id=>registration.IssueManagedRegistrationCodeAsync(id,timeout.Token),timeout.Token);
                    lock(sync) registrations[item.Host.Id]=result;
                    Set(item.Host,result.Registered ? "Registered" : "Needs attention",result.Message);
                }
                catch(Exception error) { Set(item.Host,"Needs attention",error is OperationCanceledException ? "Registration timed out. Check Portal before retrying; an accepted registration is retained." : error.Message); }
            }
        }
        catch(Exception error)
        { foreach(var item in Results.Where(x=>x.State=="Queued")) Set(item.Host,"Needs attention",error.Message); }
        finally { lock(sync) running=false; Notify(); }
    }
    private void Set(ManagedHost host,string state,string detail)
    { lock(sync) results=results.Select(x=>x.Host.Id==host.Id ? new(host,state,detail):x).ToArray(); Notify(); }
    private void Notify()
    { foreach(var callback in Changed?.GetInvocationList() ?? []) try { ((Action)callback)(); } catch(InvalidOperationException){} }
}
