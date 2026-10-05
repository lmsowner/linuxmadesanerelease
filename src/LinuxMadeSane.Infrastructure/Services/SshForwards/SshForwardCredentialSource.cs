// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace LinuxMadeSane.Infrastructure.Services.SshForwards;
// Runtime access is restricted to credential IDs in administrator-approved forward definitions.
// Browser requests still use the regular credential service and its session authorisation.
public interface ISshForwardCredentialSource
{
    Task<string> RevisionAsync(Guid id, CancellationToken token);
    Task<SavedConnectionCredential> ResolveAsync(Guid id, CancellationToken token);
}
public sealed class SshForwardCredentialSource(IServiceScopeFactory scopes) : ISshForwardCredentialSource
{
    public async Task<string> RevisionAsync(Guid id, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var entity = await scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>().SavedConnectionCredentials
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return entity?.UpdatedAtUtc.ToString("O") ?? "missing";
    }
    public async Task<SavedConnectionCredential> ResolveAsync(Guid id, CancellationToken token)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
        var e = await db.SavedConnectionCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new InvalidOperationException("The saved credential was removed. Select an available credential for this forward.");
        if (e.Kind == ConnectionCredentialKind.Smb) throw new InvalidOperationException("An SSH forward cannot use SMB credentials.");
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        async Task<string> Read(string? reference) => string.IsNullOrEmpty(reference) ? "" : await secrets.ResolveSecretAsync(reference, token) ?? "";
        var credential = new SavedConnectionCredential(new(e.Id, e.Name, e.Kind, e.Server, e.Port, e.Username, e.Domain, e.PublicKey,
            e.PasswordReference is not null, e.PrivateKeyReference is not null, e.PassphraseReference is not null, e.UpdatedAtUtc),
            await Read(e.PasswordReference), await Read(e.PrivateKeyReference), await Read(e.PassphraseReference));
        if (credential.Password.Length == 0 && credential.PrivateKey.Length == 0)
            throw new InvalidOperationException("The saved SSH credential has no usable password or key. Edit and test it in Credentials.");
        db.SavedCredentialAudits.Add(new SavedCredentialAuditEntity { Id = Guid.NewGuid(), CredentialId = id,
            Action = "SSH forward credentials used", OccurredAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(token);
        return credential;
    }
}
