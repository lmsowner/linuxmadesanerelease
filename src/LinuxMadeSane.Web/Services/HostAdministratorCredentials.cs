// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public interface IHostAdministratorCredentials
{
    Task<CommandExecutionInput?> ResolveAsync(Guid hostId, string username, CancellationToken token = default);
    Task SaveAsync(Guid hostId, string username, string password, CancellationToken token = default);
}

public sealed class HostAdministratorCredentials(IManagedHostStore hosts, ISecretStore secrets,
    ISavedConnectionCredentialService vault, ISavedCredentialAccessContext access) : IHostAdministratorCredentials
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public static bool Matches(SavedConnectionCredentialSummary credential, ManagedHost host, string username) =>
        credential.Kind is ConnectionCredentialKind.Ssh or ConnectionCredentialKind.Sftp && credential.HasPassword &&
        credential.Port == host.Port && credential.Username == username &&
        string.Equals(credential.Server.Trim().TrimEnd('.'), host.Hostname.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    public async Task<CommandExecutionInput?> ResolveAsync(Guid hostId, string username, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return await ResolveCoreAsync(hostId, username, token); }
        finally { gate.Release(); }
    }

    private async Task<CommandExecutionInput?> ResolveCoreAsync(Guid hostId, string username, CancellationToken token)
    {
        var actor = await access.GetAuthenticatedUserIdAsync(token);
        if (actor is null) return null;
        var host = await hosts.GetAsync(hostId, token);
        if (host is null) return null;
        var candidates = (await vault.ListAsync(actor.Value, token)).Where(c => Matches(c, host, username)).ToArray();
        string? password = null;
        // Never choose an arbitrary account, hostname alias, key passphrase or SMB password.
        if (candidates.Length == 1)
            password = (await vault.ResolveAsync(actor.Value, candidates[0].Id, token))?.Password;
        else if (host.Username == username && !string.IsNullOrWhiteSpace(host.PasswordSecretReference))
            password = await secrets.ResolveSecretAsync(host.PasswordSecretReference, token);
        return string.IsNullOrEmpty(password) ? null : new CommandExecutionInput(password + "\n", true);
    }

    public async Task SaveAsync(Guid hostId, string username, string password, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { await SaveCoreAsync(hostId, username, password, token); }
        finally { gate.Release(); }
    }

    private async Task SaveCoreAsync(Guid hostId, string username, string password, CancellationToken token)
    {
        var actor = await access.GetAuthenticatedUserIdAsync(token) ?? throw new UnauthorizedAccessException("Sign in before saving administrator credentials.");
        var host = await hosts.GetAsync(hostId, token) ?? throw new InvalidOperationException("Host not found.");
        var candidates = (await vault.ListAsync(actor, token)).Where(c => Matches(c, host, username)).ToArray();
        if (candidates.Length > 1) throw new InvalidOperationException("Several saved credentials match this account. Choose which to update in the credential store.");
        var name = candidates.SingleOrDefault()?.Name ?? $"{host.Name} · {username} sudo";
        await vault.SaveAsync(actor, new SavedConnectionCredentialEditor
        {
            Id = candidates.SingleOrDefault()?.Id, Name = name.Length > 80 ? name[..80] : name,
            Kind = candidates.SingleOrDefault()?.Kind ?? ConnectionCredentialKind.Ssh, Server = host.Hostname, Port = host.Port,
            Username = username, Password = password
        }, token);
    }
}
