// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

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
        await Authorize(userId, editor.StoredCredentialId, token);
        if (editor.Id.HasValue && editor.CopyFromId.HasValue)
            throw new InvalidOperationException("A duplicate must be saved as a new entry.");
        var source = editor.StoredCredentialId is Guid sourceId
            ? await db.SavedConnectionCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token)
                ?? throw new InvalidOperationException("Credential not found.")
            : null;
        var existing = editor.Id.HasValue ? source : null;
        if (source is not null && !source.Kind.CanChangeTo(editor.Kind))
            throw new InvalidOperationException("Create a new credential to change its connection protocol.");
        var name = editor.Name.Trim(); var server = editor.Server.Trim(); var username = editor.Username.Trim();
        if (name.Length is 0 or > 80 || server.Length > 255 || username.Length > 128 || editor.Domain.Length > 128 || !Enum.IsDefined(editor.Kind) || editor.UsesConnectionPort && editor.Port is < 1 or > 65535)
            throw new InvalidOperationException("Check the name, server, username and port.");
        if ((name + username + editor.Domain).Any(char.IsControl)) throw new InvalidOperationException("Connection details cannot contain control characters.");
        if (server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or ',')) throw new InvalidOperationException("Enter a hostname or IP address, without a path.");
        if (editor.Kind != ConnectionCredentialKind.SshKeyPair && (server.Length == 0 || editor.Kind != ConnectionCredentialKind.Vnc && username.Length == 0)) throw new InvalidOperationException("Server and username are required.");
        if (editor.Kind == ConnectionCredentialKind.Smb && (editor.Domain + username + editor.Password).Any(c => c is '\r' or '\n' or '\0')) throw new InvalidOperationException("SMB credentials cannot contain line breaks.");
        var auth = editor.Kind.SshAuthentication(!editor.ClearPrivateKey && (editor.PrivateKey.Length > 0 || source?.PrivateKeyReference is not null));
        var isKey = ManagedHostAuthenticationPolicy.UsesPrivateKeyMaterial(auth);
        var needsPassword = ManagedHostAuthenticationPolicy.UsesPassword(auth);
        var password = !needsPassword || editor.ClearPassword ? "" : editor.Password.Length > 0 ? editor.Password : await Read(source?.PasswordReference, token);
        var privateKey = !isKey || editor.ClearPrivateKey ? "" : editor.PrivateKey.Length > 0 ? editor.PrivateKey : await Read(source?.PrivateKeyReference, token);
        var passphrase = !isKey || editor.ClearPassphrase ? "" : editor.Passphrase.Length > 0 ? editor.Passphrase : await Read(source?.PassphraseReference, token);
        var publicKey = "";
        if (isKey && privateKey.Length == 0)
        {
            // Older vaults can contain standalone public keys. Their copies retain that identity.
            if (editor.Kind != ConnectionCredentialKind.SshKeyPair || source?.Kind != ConnectionCredentialKind.SshKeyPair ||
                source.PrivateKeyReference is not null || string.IsNullOrWhiteSpace(source.PublicKey))
                throw new InvalidOperationException("Paste or load a private key, or generate a keypair.");
            publicKey = source.PublicKey;
            passphrase = "";
        }
        else if (isKey)
        {
            using var key = SshPrivateKeyReader.Read(privateKey, passphrase, out privateKey);
            publicKey = SshPrivateKeyReader.PublicKey(key);
        }
        if (editor.Kind == ConnectionCredentialKind.SshKeyAndPassword && string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Enter the remote account password.");
        if (needsPassword && password.Length == 0) throw new InvalidOperationException(editor.Kind == ConnectionCredentialKind.Sftp ? "Enter a password or import or generate a private key for SFTP authentication." : "Enter a password.");
        var item = new SavedConnectionCredentialEntity { Id = existing?.Id ?? Guid.NewGuid(), UserId = existing?.UserId ?? userId, Name = name, Kind = editor.Kind, Server = server,
            Port = !editor.UsesConnectionPort ? 0 : editor.Kind == ConnectionCredentialKind.Smb ? 445 : editor.Port, Username = username, Domain = editor.Kind is ConnectionCredentialKind.Smb or ConnectionCredentialKind.Rdp ? editor.Domain.Trim() : "",
            PublicKey = publicKey, CreatedAtUtc = existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        var created = new List<string>();
        SavedCredentialAuditEntity? savedAudit = null;
        async Task<string?> Store(string value, string? old, bool unchanged, string purpose)
        {
            if (unchanged && old is not null) return old;
            if (value.Length == 0) return null;
            var reference = await secrets.StoreSecretAsync(value, purpose, token); created.Add(reference); return reference;
        }
        try
        {
            item.PasswordReference = await Store(needsPassword ? password : "", existing?.PasswordReference, needsPassword && editor.Password.Length == 0 && !editor.ClearPassword, "Saved connection password");
            item.PrivateKeyReference = await Store(isKey ? privateKey : "", existing?.PrivateKeyReference, isKey && editor.PrivateKey.Length == 0, "Saved SSH private key");
            item.PassphraseReference = await Store(isKey ? passphrase : "", existing?.PassphraseReference, isKey && passphrase.Length > 0 && editor.Passphrase.Length == 0 && !editor.ClearPassphrase, "Saved SSH key passphrase");
            var tracked = db.ChangeTracker.Entries<SavedConnectionCredentialEntity>().FirstOrDefault(x => x.Entity.Id == item.Id);
            if (tracked is not null) tracked.State = EntityState.Detached;
            if (existing is null) db.SavedConnectionCredentials.Add(item); else db.SavedConnectionCredentials.Update(item);
            savedAudit = Record(userId, item.Id, editor.CopyFromId.HasValue ? "Duplicated" : existing is null ? "Created" : "Updated");
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
    private static SavedConnectionCredentialSummary Summary(SavedConnectionCredentialEntity x) => new(x.Id,x.Name,x.Kind,x.Server,x.Kind is ConnectionCredentialKind.Rdp or ConnectionCredentialKind.Vnc ? 0 : x.Port,x.Username,x.Domain,x.PublicKey,x.PasswordReference is not null,x.PrivateKeyReference is not null,x.PassphraseReference is not null,x.UpdatedAtUtc);
}
