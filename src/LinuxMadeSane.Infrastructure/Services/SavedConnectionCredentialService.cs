// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SavedConnectionCredentialService(LinuxMadeSaneDbContext db, ISecretStore secrets, ISavedCredentialAccessContext access) : ISavedConnectionCredentialService
{
    public async Task<IReadOnlyList<SavedConnectionCredentialSummary>> ListAsync(Guid userId, CancellationToken token = default)
    {
        await Authorize(userId, null, token);
        return (await db.SavedConnectionCredentials.AsNoTracking().OrderBy(x => x.Name).ToArrayAsync(token)).Select(Summary).ToArray();
    }
    public async Task<IReadOnlyList<SavedCredentialAudit>> AuditAsync(Guid userId, CancellationToken token = default)
    {
        await Authorize(userId, null, token);
        return (await db.SavedCredentialAudits.FromSqlInterpolated($"SELECT * FROM saved_credential_audit ORDER BY OccurredAtUtc DESC LIMIT 50")
            .AsNoTracking().ToArrayAsync(token)).Select(x => new SavedCredentialAudit(x.CredentialId, x.Action, x.OccurredAtUtc)).ToArray();
    }
    private SavedCredentialAuditEntity Record(Guid? actor, Guid? credentialId, string action)
    {
        var entry = new SavedCredentialAuditEntity { Id=Guid.NewGuid(), ActorUserId=actor, CredentialId=credentialId, Action=action, OccurredAtUtc=DateTimeOffset.UtcNow };
        db.SavedCredentialAudits.Add(entry);
        return entry;
    }
    private async Task Authorize(Guid owner, Guid? id, CancellationToken token)
    {
        var actor = await access.GetAuthenticatedUserIdAsync(token);
        if (actor is null || actor != owner)
        {
            Record(actor, id, "Access denied"); await db.SaveChangesAsync(token);
            throw new UnauthorizedAccessException("This session is not authorised to use the LMS credential store.");
        }
    }

    public async Task<SavedConnectionCredential?> ResolveAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        await Authorize(userId, id, token);
        var item = await db.SavedConnectionCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (item is null) { Record(userId,id,"Not found"); await db.SaveChangesAsync(token); return null; }
        var resolved = new SavedConnectionCredential(Summary(item), await Read(item.PasswordReference, token), await Read(item.PrivateKeyReference, token), await Read(item.PassphraseReference, token));
        Record(userId,id,"Used"); await db.SaveChangesAsync(token);
        return resolved;
    }

    public async Task<SavedConnectionCredentialSummary> SaveAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default)
    {
        await Authorize(userId, editor.Id, token);
        var existing = editor.Id.HasValue ? await db.SavedConnectionCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == editor.Id, token) : null;
        if (editor.Id.HasValue && existing is null) throw new InvalidOperationException("Credential not found.");
        if (existing is not null && existing.Kind != editor.Kind) throw new InvalidOperationException("Create a new credential to change its type.");
        var name = editor.Name.Trim(); var server = editor.Server.Trim(); var username = editor.Username.Trim();
        if (name.Length is 0 or > 80 || server.Length > 255 || username.Length > 128 || editor.Domain.Length > 128 || !Enum.IsDefined(editor.Kind) || editor.Port is < 1 or > 65535)
            throw new InvalidOperationException("Check the name, server, username and port.");
        if ((name + username + editor.Domain).Any(char.IsControl)) throw new InvalidOperationException("Connection details cannot contain control characters.");
        if (server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or ',')) throw new InvalidOperationException("Enter a hostname or IP address, without a path.");
        if (editor.Kind != ConnectionCredentialKind.SshKeyPair && (server.Length == 0 || username.Length == 0)) throw new InvalidOperationException("Server and username are required.");
        if (editor.Kind == ConnectionCredentialKind.Smb && (editor.Domain + username + editor.Password).Any(c => c is '\r' or '\n' or '\0')) throw new InvalidOperationException("SMB credentials cannot contain line breaks.");
        var isKey = editor.Kind == ConnectionCredentialKind.SshKeyPair;
        var password = editor.ClearPassword ? "" : editor.Password.Length > 0 ? editor.Password : await Read(existing?.PasswordReference, token);
        var privateKey = editor.PrivateKey.Length > 0 ? editor.PrivateKey : await Read(existing?.PrivateKeyReference, token);
        var passphrase = editor.ClearPassphrase ? "" : editor.Passphrase.Length > 0 ? editor.Passphrase : await Read(existing?.PassphraseReference, token);
        var publicKey = "";
        if (isKey)
        {
            if (privateKey.Length == 0) throw new InvalidOperationException("Import a private key or generate a keypair.");
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKey));
                using var key = new PrivateKeyFile(stream, passphrase);
                var data = key.HostKeyAlgorithms.First().Data;
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                publicKey = System.Text.Encoding.UTF8.GetString(data, 4, length) + " " + Convert.ToBase64String(data);
            }
            catch { throw new InvalidOperationException("The private key or passphrase is invalid."); }
        }
        else if (password.Length == 0) throw new InvalidOperationException("Enter a password.");
        var item = new SavedConnectionCredentialEntity { Id = existing?.Id ?? Guid.NewGuid(), UserId = existing?.UserId ?? userId, Name = name, Kind = editor.Kind, Server = server,
            Port = editor.Kind == ConnectionCredentialKind.Smb ? 445 : editor.Port, Username = username, Domain = editor.Kind == ConnectionCredentialKind.Smb ? editor.Domain.Trim() : "",
            PublicKey = publicKey, CreatedAtUtc = existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        var created = new List<string>();
        SavedCredentialAuditEntity? savedAudit = null;
        async Task<string?> Store(string value, string? old, bool unchanged, string purpose)
        {
            if (unchanged) return old;
            if (value.Length == 0) return null;
            var reference = await secrets.StoreSecretAsync(value, purpose, token); created.Add(reference); return reference;
        }
        try
        {
            item.PasswordReference = await Store(isKey ? "" : password, existing?.PasswordReference, !isKey && editor.Password.Length == 0 && !editor.ClearPassword, "Saved connection password");
            item.PrivateKeyReference = await Store(isKey ? privateKey : "", existing?.PrivateKeyReference, isKey && editor.PrivateKey.Length == 0, "Saved SSH private key");
            item.PassphraseReference = await Store(isKey ? passphrase : "", existing?.PassphraseReference, isKey && editor.Passphrase.Length == 0 && !editor.ClearPassphrase, "Saved SSH key passphrase");
            var tracked = db.ChangeTracker.Entries<SavedConnectionCredentialEntity>().FirstOrDefault(x => x.Entity.Id == item.Id);
            if (tracked is not null) tracked.State = EntityState.Detached;
            if (existing is null) db.SavedConnectionCredentials.Add(item); else db.SavedConnectionCredentials.Update(item);
            savedAudit = Record(userId,item.Id,existing is null ? "Created" : "Updated");
            await db.SaveChangesAsync(token);
        }
        catch
        {
            db.Entry(item).State = EntityState.Detached;
            if (savedAudit is not null) db.Entry(savedAudit).State = EntityState.Detached;
            foreach (var reference in created) await secrets.DeleteSecretAsync(reference, CancellationToken.None);
            throw;
        }
        if (existing is not null)
        {
            var keep = new[] { item.PasswordReference, item.PrivateKeyReference, item.PassphraseReference };
            foreach (var reference in References(existing).Except(keep)) await secrets.DeleteSecretAsync(reference!, token);
        }
        return Summary(item);
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        await Authorize(userId, id, token);
        var item = await db.SavedConnectionCredentials.SingleOrDefaultAsync(x => x.Id == id, token);
        if (item is null) return;
        db.SavedConnectionCredentials.Remove(item); Record(userId,id,"Deleted"); await db.SaveChangesAsync(token);
        foreach (var reference in References(item)) await secrets.DeleteSecretAsync(reference!, token);
    }
    private static IEnumerable<string?> References(SavedConnectionCredentialEntity item) => new[] { item.PasswordReference, item.PrivateKeyReference, item.PassphraseReference }.Where(x => x is not null);
    private async Task<string> Read(string? reference, CancellationToken token) => reference is null ? "" : await secrets.ResolveSecretAsync(reference, token) ?? throw new InvalidOperationException("Stored credential material is unavailable.");
    private static SavedConnectionCredentialSummary Summary(SavedConnectionCredentialEntity x) => new(x.Id,x.Name,x.Kind,x.Server,x.Port,x.Username,x.Domain,x.PublicKey,x.PasswordReference is not null,x.PrivateKeyReference is not null,x.PassphraseReference is not null,x.UpdatedAtUtc);
}
