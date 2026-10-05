// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Infrastructure.Services.SshForwards;
public sealed class SshPortForwardService(SshForwardStore store, SshForwardSupervisor supervisor,
    ISavedConnectionCredentialService credentials, ISavedCredentialAccessContext access,
    IEdgeGatewayService gateway, ISshForwardProcessFactory processes) : ISshPortForwardService
{
    private static readonly SemaphoreSlim Operations = new(1, 1);
    private async Task<Guid> AuthorizeAsync(CancellationToken token) => await access.GetAuthenticatedUserIdAsync(token)
        ?? throw new UnauthorizedAccessException("This session cannot administer SSH forwards.");
    public async Task<IReadOnlyList<SshForwardView>> ListAsync(CancellationToken token = default)
    {
        await AuthorizeAsync(token);
        return (await store.ReadAsync(token)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new SshForwardView(x, supervisor.Status(x.Id, x.Enabled))).ToArray();
    }
    public async Task TestConnectionAsync(SshPortForward input, CancellationToken token = default)
    {
        var actor = await AuthorizeAsync(token);
        var credential = await credentials.ResolveAsync(actor, input.CredentialId, token)
            ?? throw new InvalidOperationException("Choose an available saved SSH credential.");
        ValidateCredential(input, credential);
        var rule = SshForwardDefinition.Validate(input with { Name = input.Name.Length > 0 ? input.Name : "SSH login test" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var process = await processes.StartAsync(rule, credential, timeout.Token, loginOnly: true);
        try
        {
            while (!await process.IsConnectedAsync(timeout.Token))
            {
                if (process.HasExited) throw new InvalidOperationException(SshForwardSupervisor.ExplainFailure(process.Failure, rule));
                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException($"SSH login to {rule.Server}:{rule.SshPort} timed out. Check the server, network route and saved credentials."); }
    }
    public async Task SaveAsync(SshPortForward input, CancellationToken token = default)
    {
        var actor = await AuthorizeAsync(token);
        await Operations.WaitAsync(token);
        try
        {
            var credential = await credentials.ResolveAsync(actor, input.CredentialId, token)
                ?? throw new InvalidOperationException("Choose an available saved SSH credential.");
            ValidateCredential(input, credential);
            var rule = SshForwardDefinition.Validate(input);
            var rules = await store.ReadAsync(token); var existing = rules.SingleOrDefault(x => x.Id == rule.Id);
            rule.EdgeGatewayRouteId = existing?.EdgeGatewayRouteId; rule.PublicUrl = existing?.PublicUrl ?? "";
            if (rule.EdgeGatewayRouteId is not null && !rule.CanLinkCaddy)
                throw new InvalidOperationException("Unlink Caddy before changing this forward away from a localhost-only local TCP listener.");
            if (rule.Enabled && rules.Any(x => x.Id != rule.Id && x.Enabled && Conflicts(x, rule)))
                throw new InvalidOperationException("Another enabled SSH forward uses this listen endpoint. Stop it or choose a different endpoint.");
            if (rule.EdgeGatewayRouteId is { } routeId)
            {
                var original = await gateway.GetEditorAsync(routeId, token);
                if (original.Id != routeId) throw new InvalidOperationException("The linked Caddy route was removed in Edge Gateway. Unlink it here, then link a new route.");
                var updated = await gateway.GetEditorAsync(routeId, token);
                updated.TargetHost = rule.ListenAddress; updated.TargetPort = rule.ListenPort; updated.Enabled = rule.Enabled;
                await ApplyRouteAsync(updated, original, token);
                try { await store.WriteAsync(rules.Where(x => x.Id != rule.Id).Append(rule), token); }
                catch { await RestoreRouteAsync(original); throw; }
            }
            else await store.WriteAsync(rules.Where(x => x.Id != rule.Id).Append(rule), token);
            if (existing is null || SshForwardDefinition.RuntimeRevision(existing) != SshForwardDefinition.RuntimeRevision(rule))
            { await supervisor.StopForwardAsync(rule.Id, token); supervisor.Restart(rule.Id); }
        }
        finally { Operations.Release(); }
    }
    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token = default)
    {
        var views = await ListAsync(token); var rule = views.SingleOrDefault(x => x.Forward.Id == id)?.Forward
            ?? throw new InvalidOperationException("This SSH forward no longer exists.");
        if (!enabled)
        {
            // Stopping must still work after a credential has been deleted.
            await Operations.WaitAsync(token);
            try
            {
                var rules = await store.ReadAsync(token); var current = Find(rules, id);
                await store.WriteAsync(rules.Select(x => x.Id == id ? x with { Enabled = false } : x), token);
                await supervisor.StopForwardAsync(id, token);
                if (current.EdgeGatewayRouteId is { } routeId)
                {
                    var original = await gateway.GetEditorAsync(routeId, token); var disabled = await gateway.GetEditorAsync(routeId, token);
                    disabled.Enabled = false;
                    try { await ApplyRouteAsync(disabled, original, token); }
                    catch (Exception ex) { throw new InvalidOperationException("The SSH forward is stopped, but its Caddy route could not be disabled. Check Edge Gateway. " + ex.Message, ex); }
                }
            }
            finally { Operations.Release(); }
        }
        else await SaveAsync(rule with { Enabled = true }, token);
    }
    public async Task RestartAsync(Guid id, CancellationToken token = default)
    {
        await AuthorizeAsync(token); var rule = Find(await store.ReadAsync(token), id);
        if (!rule.Enabled) throw new InvalidOperationException("Start this forward before restarting it.");
        await supervisor.StopForwardAsync(id, token); supervisor.Restart(id);
    }
    public async Task DeleteAsync(Guid id, CancellationToken token = default)
    {
        await AuthorizeAsync(token); await Operations.WaitAsync(token);
        try
        {
            var rules = await store.ReadAsync(token); var rule = Find(rules, id);
            if (rule.EdgeGatewayRouteId is not null) throw new InvalidOperationException("Unlink its Caddy route before removing this SSH forward.");
            await store.WriteAsync(rules.Where(x => x.Id != id), token); await supervisor.StopForwardAsync(id, token);
        }
        finally { Operations.Release(); }
    }
    public async Task LinkCaddyAsync(Guid id, SshForwardCaddyLink link, CancellationToken token = default)
    {
        await AuthorizeAsync(token); await Operations.WaitAsync(token);
        try
        {
            var rules = await store.ReadAsync(token); var rule = Find(rules, id);
            if (!rule.CanLinkCaddy) throw new InvalidOperationException("Caddy linking needs a local TCP forward bound to 127.0.0.1 or ::1.");
            if (rule.EdgeGatewayRouteId is not null) throw new InvalidOperationException("This forward is already linked. Maintain its public policy in Edge Gateway.");
            var route = new EdgeGatewayRouteEditor { DisplayName = rule.Name, Hostname = link.Hostname, DomainName = link.DomainName,
                TargetHost = rule.ListenAddress, TargetPort = rule.ListenPort, TargetScheme = link.Scheme, AuthMode = link.AuthMode,
                Enabled = rule.Enabled, SkipUpstreamTlsVerification = false, Notes = $"SSH forward {rule.Id}: {rule.Description}" };
            var routeId = await gateway.SaveRouteAsync(route, token);
            try
            {
                var savedRoute = await gateway.GetEditorAsync(routeId, token);
                var result = await gateway.ApplyCaddyConfigurationAsync(token);
                if (!result.Success) throw new InvalidOperationException(result.Summary);
                await store.WriteAsync(rules.Select(x => x.Id == id ? x with { EdgeGatewayRouteId = routeId,
                    PublicUrl = "https://" + savedRoute.Hostname } : x), token);
            }
            catch
            {
                await gateway.DeleteRouteAsync(routeId, CancellationToken.None);
                await gateway.ApplyCaddyConfigurationAsync(CancellationToken.None); throw;
            }
        }
        finally { Operations.Release(); }
    }
    public async Task UnlinkCaddyAsync(Guid id, CancellationToken token = default)
    {
        await AuthorizeAsync(token); await Operations.WaitAsync(token);
        try
        {
            var rules = await store.ReadAsync(token); var rule = Find(rules, id);
            if (rule.EdgeGatewayRouteId is not { } routeId) return;
            var previous = await gateway.GetEditorAsync(routeId, token);
            if (previous.Id != routeId)
            {
                await store.WriteAsync(rules.Select(x => x.Id == id ? x with { EdgeGatewayRouteId = null, PublicUrl = "" } : x), token);
                return;
            }
            await gateway.DeleteRouteAsync(routeId, token);
            try
            {
                var result = await gateway.ApplyCaddyConfigurationAsync(token);
                if (!result.Success) throw new InvalidOperationException(result.Summary);
                await store.WriteAsync(rules.Select(x => x.Id == id ? x with { EdgeGatewayRouteId = null, PublicUrl = "" } : x), token);
            }
            catch { await RestoreRouteAsync(previous); throw; }
        }
        finally { Operations.Release(); }
    }
    private async Task ApplyRouteAsync(EdgeGatewayRouteEditor updated, EdgeGatewayRouteEditor original, CancellationToken token)
    {
        try
        {
            await gateway.SaveRouteAsync(updated, token);
            var applied = await gateway.ApplyCaddyConfigurationAsync(token);
            if (!applied.Success) throw new InvalidOperationException(applied.Summary);
        }
        catch { await RestoreRouteAsync(original); throw; }
    }
    private async Task RestoreRouteAsync(EdgeGatewayRouteEditor original)
    {
        await gateway.SaveRouteAsync(original, CancellationToken.None);
        var restored = await gateway.ApplyCaddyConfigurationAsync(CancellationToken.None);
        if (!restored.Success) throw new InvalidOperationException("The previous Edge Gateway route could not be restored. Check Edge Gateway before retrying.");
    }
    private static SshPortForward Find(IReadOnlyList<SshPortForward> rules, Guid id) => rules.SingleOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("This SSH forward no longer exists.");
    internal static bool Conflicts(SshPortForward a, SshPortForward b)
    {
        if (a.IsRemote != b.IsRemote || a.ListenKind != b.ListenKind) return false;
        if (a.IsRemote && (!a.Server.Equals(b.Server, StringComparison.OrdinalIgnoreCase) || a.SshPort != b.SshPort)) return false;
        if (a.ListenKind == SshForwardEndpointKind.Tcp && (a.ListenPort == 0 || b.ListenPort == 0)) return false;
        return a.ListenKind == SshForwardEndpointKind.UnixSocket ? a.ListenSocket == b.ListenSocket :
            a.ListenPort == b.ListenPort && (a.ListenAddress == b.ListenAddress || a.ListenAddress is "*" or "0.0.0.0" or "::" || b.ListenAddress is "*" or "0.0.0.0" or "::");
    }
    public static void ValidateCredential(SshPortForward rule, SavedConnectionCredential credential)
    {
        var summary = credential.Summary;
        if (summary.Kind == ConnectionCredentialKind.Smb) throw new InvalidOperationException("Choose SSH, SFTP or SSH keypair credentials.");
        if (summary.Server.Length > 0 && (!summary.Server.TrimEnd('.').Equals(rule.Server.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase) || summary.Port != rule.SshPort))
            throw new InvalidOperationException("Use the server and SSH port saved with this credential, or select a reusable keypair.");
        if (summary.Username.Length > 0 && summary.Username != rule.Username)
            throw new InvalidOperationException("Use the username saved with this credential.");
    }
}
