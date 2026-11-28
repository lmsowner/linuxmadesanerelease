// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
namespace LinuxMadeSane.Web.Services;

// One check after startup keeps an already registered connector coordinated with
// CE updates. Never activate/register a new instance or change release channels.
public sealed class PortalAddonUpdateService(IServiceScopeFactory scopes,ApplicationUpdateService updates,
    ILogger<PortalAddonUpdateService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30),stoppingToken);
            using var scope=scopes.CreateScope();
            var addons=scope.ServiceProvider.GetRequiredService<PortalAddonBootstrapService>();
            if(await addons.StatusAsync(stoppingToken) is not {IsEnabled:true,PortalInstanceId:not null})return;
            // UpdateAsync verifies the signed manifest, compatibility and channel,
            // downloads only a newer connector, and uses its normal atomic install.
            // A successful install restarts LMS once. The version marker prevents
            // another install/restart on the following startup.
            var message=await addons.UpdateAsync(updates.GetStatus().Channel,token:stoppingToken,onlyIfNewer:true);
            logger.LogInformation("Portal connector startup check: {Message}",message);
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        catch(Exception error)
        {
            logger.LogWarning(error,"Portal connector update could not complete. The existing connector and local LMS remain available; retry from About > Licensing > Manage Portal registration.");
        }
    }
}
