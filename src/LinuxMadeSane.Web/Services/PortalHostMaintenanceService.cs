// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Collections.Concurrent;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Updates;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
namespace LinuxMadeSane.Web.Services;

public sealed record PortalHostMaintenanceRequest(string Token,Guid RequestId,string Action,bool RebootIfNeeded=false,bool StatusOnly=false,bool Confirmed=false);
public sealed record PortalHostMaintenanceStatus(Guid RequestId,string Action,string State,string Message,string BootId,
    DateTimeOffset RequestedAtUtc,DateTimeOffset UpdatedAtUtc,bool RebootIfNeeded,string InitialVersion);

/// <summary>Idempotent receipts for three existing LMS actions. Contains no shell commands or Portal credentials.</summary>
public sealed class PortalHostMaintenanceService(IServiceScopeFactory scopes,IHostApplicationLifetime lifetime,ILogger<PortalHostMaintenanceService> log)
{
    private readonly SemaphoreSlim gate=new(1);
    private readonly ConcurrentDictionary<Guid,Task> active=new();
    public static bool ValidAction(string action)=>action is "lms-update" or "package-update" or "reboot";
    public async Task<PortalHostMaintenanceStatus?> RequestAsync(PortalHostMaintenanceRequest request,CancellationToken token)
    {
        if(request.RequestId==Guid.Empty||!ValidAction(request.Action)||!request.StatusOnly&&!request.Confirmed)throw new ArgumentException("Confirm this maintenance action.");
        await gate.WaitAsync(token);
        try
        {
            using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
            var key="portal-maintenance-"+request.RequestId.ToString("N");
            var entry=await db.InfrastructureStates.SingleOrDefaultAsync(s=>s.Key==key,token);
            if(entry is not null)
            {
                var status=JsonSerializer.Deserialize<PortalHostMaintenanceStatus>(entry.Json)!;
                if(status.Action!=request.Action||status.RebootIfNeeded!=request.RebootIfNeeded)throw new ArgumentException("This request identifier belongs to another maintenance action.");
                var currentVersion=scope.ServiceProvider.GetRequiredService<ApplicationUpdateService>().GetStatus().CurrentVersion;
                if(status.State=="Rebooting"&&status.BootId!=BootId())status=status with{State="Completed",Message="Server reboot completed",UpdatedAtUtc=DateTimeOffset.UtcNow};
                else if(status.State=="Updating LMS"&&currentVersion!=status.InitialVersion)status=status with{State="Completed",Message="LMS updated to "+currentVersion,UpdatedAtUtc=DateTimeOffset.UtcNow};
                else if(status.State is "Running" or "Requested"&&!active.ContainsKey(request.RequestId))status=status with{State="Interrupted",Message="The previous operation was interrupted. Review this host before requesting it again.",UpdatedAtUtc=DateTimeOffset.UtcNow};
                if(entry.Json!=JsonSerializer.Serialize(status)){entry.Json=JsonSerializer.Serialize(status);entry.UpdatedAtUtc=DateTimeOffset.UtcNow;await db.SaveChangesAsync(token);}
                if(status.State=="Running"&&status.Action=="package-update")
                {var job=scope.ServiceProvider.GetRequiredService<IHostSystemUpdateService>().GetSnapshot().Job;return status with{Message=job.Summary+" · "+job.ProgressPercent+"%"};}
                return status;
            }
            if(request.StatusOnly)return null;
            if(!active.IsEmpty)throw new InvalidOperationException("Another Portal maintenance action is running on this server.");
            if(scope.ServiceProvider.GetRequiredService<ApplicationUpdateService>().GetStatus().State==ApplicationUpdateState.Installing ||
                scope.ServiceProvider.GetRequiredService<IHostSystemUpdateService>().GetSnapshot().Job.State is HostSystemUpdateJobState.Applying or HostSystemUpdateJobState.Refreshing)
                throw new InvalidOperationException("Another update is running on this server. Wait for it to finish before requesting maintenance.");
            var now=DateTimeOffset.UtcNow;var receipt=new PortalHostMaintenanceStatus(request.RequestId,request.Action,"Requested","Maintenance request accepted",BootId(),now,now,request.RebootIfNeeded,
                scope.ServiceProvider.GetRequiredService<ApplicationUpdateService>().GetStatus().CurrentVersion);
            db.InfrastructureStates.Add(new(){Key=key,Json=JsonSerializer.Serialize(receipt),UpdatedAtUtc=now});await db.SaveChangesAsync(token);
            // A crash after the receipt is saved never silently replays a reboot or package operation.
            var work=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);active[receipt.RequestId]=work.Task;
            _=Task.Run(async()=>{try{await RunAsync(receipt);work.TrySetResult();}catch(Exception e){work.TrySetException(e);log.LogWarning("Portal maintenance {Id} stopped: {Type}",receipt.RequestId,e.GetType().Name);}finally{active.TryRemove(receipt.RequestId,out _);}});
            return receipt;
        }
        finally{gate.Release();}
    }
    private async Task SaveAsync(PortalHostMaintenanceStatus status)
    {
        await gate.WaitAsync();try{using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();var entry=await db.InfrastructureStates.SingleAsync(s=>s.Key=="portal-maintenance-"+status.RequestId.ToString("N"));entry.Json=JsonSerializer.Serialize(status with{UpdatedAtUtc=DateTimeOffset.UtcNow});entry.UpdatedAtUtc=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}finally{gate.Release();}
    }
    private async Task RunAsync(PortalHostMaintenanceStatus receipt)
    {
        var status=receipt;
        try
        {
            await Task.Delay(1500,lifetime.ApplicationStopping); // Let the accepted response leave before a restart/reboot.
            status=status with{State="Running",Message="Applying "+receipt.Action};await SaveAsync(status);
            using var scope=scopes.CreateScope();var packages=scope.ServiceProvider.GetRequiredService<IHostSystemUpdateService>();
            if(receipt.Action=="lms-update")
            {
                var updates=scope.ServiceProvider.GetRequiredService<ApplicationUpdateService>();
                status=status with{State="Updating LMS",Message="Updating LMS using this host’s saved release channel"};await SaveAsync(status);
                var result=await updates.InstallLatestAsync(lifetime.ApplicationStopping);
                status=status with{State=result.State==ApplicationUpdateState.Failed?"Failed":result.State==ApplicationUpdateState.Installing?"Updating LMS":"Completed",Message=result.Summary};
            }
            else if(receipt.Action=="package-update")
            {
                if(packages.GetSnapshot().Job.State is HostSystemUpdateJobState.Applying or HostSystemUpdateJobState.Refreshing)throw new InvalidOperationException("Another package operation is running.");
                var refreshed=await packages.RefreshAsync(true,lifetime.ApplicationStopping);
                if(refreshed.Job.State==HostSystemUpdateJobState.Failed||refreshed.Job.NeedsAttention)throw new InvalidOperationException("Package sources need attention. Review the host update log.");
                var result=await packages.ApplyPackageUpdatesAsync(HostPackageUpdateMode.AllPackages,false,lifetime.ApplicationStopping);
                if(result.Job.State!=HostSystemUpdateJobState.Completed||result.Job.NeedsAttention)throw new InvalidOperationException("Package updates need attention. Review the host update log.");
                status=status with{State="Completed",Message=result.RebootRequired?"Package updates completed; a reboot is required":"Package updates completed"};
                if(receipt.RebootIfNeeded&&result.RebootRequired)
                {status=status with{State="Rebooting",Message="Package updates completed; rebooting because Linux requires it"};await SaveAsync(status);await packages.RebootAsync(lifetime.ApplicationStopping);}
            }
            else
            {status=status with{State="Rebooting",Message="Reboot requested"};await SaveAsync(status);await packages.RebootAsync(lifetime.ApplicationStopping);}
            await SaveAsync(status);
        }
        catch(OperationCanceledException)when(lifetime.ApplicationStopping.IsCancellationRequested){} // Receipt is checked on the next authenticated poll.
        catch(Exception e)
        {await SaveAsync(status with{State="Failed",Message=e is InvalidOperationException?e.Message:"Maintenance could not finish. Check the LMS update log and saved runner access."});log.LogWarning("Portal maintenance {Id} failed: {Type}",receipt.RequestId,e.GetType().Name);}
    }
    private static string BootId(){try{return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();}catch(IOException){return "unavailable";}}
}
