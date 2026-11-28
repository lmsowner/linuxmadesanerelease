// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Infrastructure.Services;

public sealed class MailRelayCredentialService(ISavedCredentialAccessContext access, ISavedConnectionCredentialService credentials, IMailRelayStore relay) : IMailRelayCredentialService
{
    private async Task<Guid> Actor(CancellationToken token) => await access.GetAuthenticatedUserIdAsync(token)
        ?? throw new UnauthorizedAccessException("Sign in to use the credential store.");
    public async Task<IReadOnlyList<SavedConnectionCredentialSummary>> ListAsync(CancellationToken token = default) =>
        (await credentials.ListAsync(await Actor(token), token)).Where(c => c.Kind is ConnectionCredentialKind.Smtp or ConnectionCredentialKind.LmsSmtp).ToArray();
    public async Task<SavedConnectionCredential> ResolveProviderAsync(Guid id, CancellationToken token = default)
    {
        var value = await credentials.ResolveAsync(await Actor(token), id, token) ?? throw new InvalidOperationException("This saved mail server no longer exists.");
        if (value.Summary.Kind != ConnectionCredentialKind.Smtp || value.Summary.Port != 587)
            throw new InvalidOperationException("Choose a mail-server credential for port 587, not a device login.");
        return value;
    }
    public async Task<SavedConnectionCredentialSummary> SaveAsync(ConnectionCredentialKind kind, string name, string host, string username, string password, CancellationToken token = default)
    {
        if (kind is not (ConnectionCredentialKind.Smtp or ConnectionCredentialKind.LmsSmtp)) throw new ArgumentException("Choose an SMTP credential type.");
        var actor = await Actor(token);
        if (kind == ConnectionCredentialKind.LmsSmtp)
        {
            var client = (await relay.ListClientsAsync(token)).SingleOrDefault(c => c.Username == username);
            if (client is null || !MailRelayTestService.VerifyPassword(password, client.PasswordHash))
                throw new InvalidOperationException("This SMTP password has been replaced. Save the current password from SMTP users instead.");
        }
        var existing = (await credentials.ListAsync(actor, token)).FirstOrDefault(c => c.Kind == kind && c.Port == 587 && c.Server.Equals(host, StringComparison.OrdinalIgnoreCase) && c.Username.Equals(username, StringComparison.Ordinal));
        if (existing is not null)
        {
            var current = await credentials.ResolveAsync(actor, existing.Id, token);
            if (current?.Password == password) return existing;
        }
        return await credentials.SaveAsync(actor, new() { Id = existing?.Id, Kind = kind, Name = name.Length > 80 ? name[..80] : name, Server = host, Port = 587, Username = username, Password = password }, token);
    }
    public async Task<string> ReadClientPasswordAsync(string host, string username, CancellationToken token = default)
    {
        var actor = await Actor(token);
        var item = (await credentials.ListAsync(actor, token)).FirstOrDefault(c => c.Kind == ConnectionCredentialKind.LmsSmtp && c.Server.Equals(host, StringComparison.OrdinalIgnoreCase) && c.Username == username)
            ?? throw new InvalidOperationException("This older SMTP password is not in Credentials. Generate and save a replacement to store it.");
        return (await credentials.ResolveAsync(actor, item.Id, token) ?? throw new InvalidOperationException("Credential no longer exists.")).Password;
    }
}
