using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.SftpServer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteSftpServerStore(LinuxMadeSaneDbContext dbContext) : ISftpServerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int SettingsRowId = 1;

    public async Task<SftpHostSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SftpHostSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SettingsRowId, cancellationToken);

        return entity is null
            ? new SftpHostSettings(
                false,
                SftpServerDefaults.BasePath,
                SftpAuthenticationMode.PublicKeyOnly,
                true,
                SftpServerDefaults.ManagedDropInPath,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null)
            : Map(entity);
    }

    public async Task SaveSettingsAsync(SftpHostSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SftpHostSettings
            .SingleOrDefaultAsync(item => item.Id == SettingsRowId, cancellationToken);

        if (entity is null)
        {
            dbContext.SftpHostSettings.Add(new SftpHostSettingsEntity
            {
                Id = SettingsRowId,
                IsManagedModeEnabled = settings.IsManagedModeEnabled,
                BasePath = settings.BasePath,
                DefaultAuthenticationMode = (int)settings.DefaultAuthenticationMode,
                PreferDropInConfiguration = settings.PreferDropInConfiguration,
                ManagedConfigPath = settings.ManagedConfigPath,
                CreatedAtUtc = settings.CreatedAtUtc,
                UpdatedAtUtc = settings.UpdatedAtUtc,
                LastAppliedAtUtc = settings.LastAppliedAtUtc
            });
        }
        else
        {
            entity.IsManagedModeEnabled = settings.IsManagedModeEnabled;
            entity.BasePath = settings.BasePath;
            entity.DefaultAuthenticationMode = (int)settings.DefaultAuthenticationMode;
            entity.PreferDropInConfiguration = settings.PreferDropInConfiguration;
            entity.ManagedConfigPath = settings.ManagedConfigPath;
            entity.CreatedAtUtc = settings.CreatedAtUtc;
            entity.UpdatedAtUtc = settings.UpdatedAtUtc;
            entity.LastAppliedAtUtc = settings.LastAppliedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SftpManagedUser>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.SftpManagedUsers
                .AsNoTracking()
                .Include(item => item.PublicKeys)
                .OrderBy(item => item.UserName)
                .ToListAsync(cancellationToken))
            .Select(Map)
            .ToArray();

    public async Task<SftpManagedUser?> GetUserAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
        var entity = await dbContext.SftpManagedUsers
            .AsNoTracking()
            .Include(item => item.PublicKeys)
            .SingleOrDefaultAsync(item => item.UserName == normalizedUserName, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveUserAsync(SftpManagedUser user, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = user.UserName.Trim().ToLowerInvariant();
        var entity = await dbContext.SftpManagedUsers
            .Include(item => item.PublicKeys)
            .SingleOrDefaultAsync(item => item.UserName == normalizedUserName, cancellationToken);

        if (entity is null)
        {
            entity = new SftpManagedUserEntity
            {
                UserName = normalizedUserName
            };
            dbContext.SftpManagedUsers.Add(entity);
        }

        entity.AuthenticationMode = (int)user.AuthenticationMode;
        entity.IsEnabled = user.IsEnabled;
        entity.HasPassword = user.HasPassword;
        entity.LoginShell = user.LoginShell;
        entity.PrimaryGroup = user.PrimaryGroup;
        entity.SupplementaryGroupsJson = Serialize(user.SupplementaryGroups);
        entity.BasePath = user.Folder.BasePath;
        entity.ChrootPath = user.Folder.ChrootPath;
        entity.WritablePath = user.Folder.WritablePath;
        entity.ChrootOwner = user.Folder.ChrootOwner;
        entity.ChrootGroup = user.Folder.ChrootGroup;
        entity.ChrootMode = user.Folder.ChrootMode;
        entity.WritableOwner = user.Folder.WritableOwner;
        entity.WritableGroup = user.Folder.WritableGroup;
        entity.WritableMode = user.Folder.WritableMode;
        entity.CreatedAtUtc = user.CreatedAtUtc;
        entity.UpdatedAtUtc = user.UpdatedAtUtc;
        entity.PasswordChangedAtUtc = user.PasswordChangedAtUtc;

        var desiredKeys = user.PublicKeys.ToDictionary(item => item.Id);
        foreach (var existingKey in entity.PublicKeys.ToList())
        {
            if (desiredKeys.ContainsKey(existingKey.Id))
            {
                continue;
            }

            dbContext.SftpPublicKeys.Remove(existingKey);
        }

        foreach (var publicKey in user.PublicKeys)
        {
            var existing = entity.PublicKeys.SingleOrDefault(item => item.Id == publicKey.Id);
            if (existing is null)
            {
                entity.PublicKeys.Add(new SftpPublicKeyEntity
                {
                    Id = publicKey.Id,
                    UserName = normalizedUserName,
                    Label = publicKey.Label,
                    KeyType = publicKey.KeyType,
                    Fingerprint = publicKey.Fingerprint,
                    PublicKeyText = publicKey.PublicKeyText,
                    CreatedAtUtc = publicKey.CreatedAtUtc,
                    UpdatedAtUtc = publicKey.UpdatedAtUtc
                });
                continue;
            }

            existing.Label = publicKey.Label;
            existing.KeyType = publicKey.KeyType;
            existing.Fingerprint = publicKey.Fingerprint;
            existing.PublicKeyText = publicKey.PublicKeyText;
            existing.CreatedAtUtc = publicKey.CreatedAtUtc;
            existing.UpdatedAtUtc = publicKey.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
        var entity = await dbContext.SftpManagedUsers
            .SingleOrDefaultAsync(item => item.UserName == normalizedUserName, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.SftpManagedUsers.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SftpAuditEntry>> ListAuditEntriesAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.SftpAuditEntries
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(Map)
            .ToArray();

    public async Task<IReadOnlyList<SftpAuditEntry>> ListAuditEntriesAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
        return (await dbContext.SftpAuditEntries
                .AsNoTracking()
                .Where(item => item.TargetKey == normalizedUserName)
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task SaveAuditEntryAsync(SftpAuditEntry entry, CancellationToken cancellationToken = default)
    {
        dbContext.SftpAuditEntries.Add(new SftpAuditEntryEntity
        {
            Id = entry.Id,
            EventType = entry.EventType,
            TargetType = entry.TargetType,
            TargetKey = entry.TargetKey,
            Summary = entry.Summary,
            Details = entry.Details,
            Success = entry.Success,
            CreatedAtUtc = entry.CreatedAtUtc,
            BackupSnapshotId = entry.BackupSnapshotId
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SftpBackupSnapshot>> ListBackupSnapshotsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.SftpBackupSnapshots
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(Map)
            .ToArray();

    public async Task<SftpBackupSnapshot?> GetBackupSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SftpBackupSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveBackupSnapshotAsync(SftpBackupSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SftpBackupSnapshots
            .SingleOrDefaultAsync(item => item.Id == snapshot.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.SftpBackupSnapshots.Add(new SftpBackupSnapshotEntity
            {
                Id = snapshot.Id,
                Summary = snapshot.Summary,
                FilesJson = JsonSerializer.Serialize(snapshot.Files, JsonOptions),
                CreatedAtUtc = snapshot.CreatedAtUtc,
                RollbackAvailable = snapshot.RollbackAvailable,
                StorageDirectory = snapshot.StorageDirectory
            });
        }
        else
        {
            entity.Summary = snapshot.Summary;
            entity.FilesJson = JsonSerializer.Serialize(snapshot.Files, JsonOptions);
            entity.CreatedAtUtc = snapshot.CreatedAtUtc;
            entity.RollbackAvailable = snapshot.RollbackAvailable;
            entity.StorageDirectory = snapshot.StorageDirectory;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SftpHostSettings Map(SftpHostSettingsEntity entity) =>
        new(
            entity.IsManagedModeEnabled,
            entity.BasePath,
            (SftpAuthenticationMode)entity.DefaultAuthenticationMode,
            entity.PreferDropInConfiguration,
            entity.ManagedConfigPath,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.LastAppliedAtUtc);

    private static SftpManagedUser Map(SftpManagedUserEntity entity) =>
        new(
            entity.UserName,
            (SftpAuthenticationMode)entity.AuthenticationMode,
            entity.IsEnabled,
            entity.HasPassword,
            entity.LoginShell,
            entity.PrimaryGroup,
            DeserializeStringList(entity.SupplementaryGroupsJson),
            new SftpUserFolder(
                entity.BasePath,
                entity.ChrootPath,
                entity.WritablePath,
                entity.ChrootOwner,
                entity.ChrootGroup,
                entity.ChrootMode,
                entity.WritableOwner,
                entity.WritableGroup,
                entity.WritableMode),
            entity.PublicKeys
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .Select(Map)
                .ToArray(),
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.PasswordChangedAtUtc);

    private static SftpPublicKey Map(SftpPublicKeyEntity entity) =>
        new(
            entity.Id,
            entity.Label,
            entity.KeyType,
            entity.Fingerprint,
            entity.PublicKeyText,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static SftpAuditEntry Map(SftpAuditEntryEntity entity) =>
        new(
            entity.Id,
            entity.EventType,
            entity.TargetType,
            entity.TargetKey,
            entity.Summary,
            entity.Details,
            entity.Success,
            entity.CreatedAtUtc,
            entity.BackupSnapshotId);

    private static SftpBackupSnapshot Map(SftpBackupSnapshotEntity entity) =>
        new(
            entity.Id,
            entity.Summary,
            JsonSerializer.Deserialize<IReadOnlyList<SftpBackupFile>>(entity.FilesJson, JsonOptions) ?? [],
            entity.CreatedAtUtc,
            entity.RollbackAvailable,
            entity.StorageDirectory);

    private static string Serialize(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), JsonOptions);

    private static IReadOnlyList<string> DeserializeStringList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<string[]>(value, JsonOptions) ?? [];
}
