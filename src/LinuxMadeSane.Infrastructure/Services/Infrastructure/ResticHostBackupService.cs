// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Infrastructure;
using LinuxMadeSane.Application.Contracts.Scheduling;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Scheduling;
using LinuxMadeSane.Core.Models.Shares;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Infrastructure.Services.Infrastructure;

public sealed class ResticHostBackupService(LinuxMadeSaneDbContext database, ISecretStore secrets,
    ILinuxCommandRunner runner, ILinuxShareModuleDataService shares,
    IServiceProvider provider, InfrastructureHostPaths hostPaths) : IHostBackupService, IScheduledTaskHandler
{
    // Cross-scope serialization also protects repository operations initiated by cron.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string DataDirectory => Path.GetDirectoryName(new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource)!;
    private async Task<List<T>> Read<T>(string key, CancellationToken token)
    {
        var entity = await database.InfrastructureStates.FindAsync([key], token);
        return entity is null ? [] : JsonSerializer.Deserialize<List<T>>(entity.Json) ?? [];
    }
    private async Task Write<T>(string key, T value, CancellationToken token)
    {
        var entity = await database.InfrastructureStates.FindAsync([key], token);
        if (entity is null) { entity = new() { Key = key }; database.InfrastructureStates.Add(entity); }
        entity.Json = JsonSerializer.Serialize(value); entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(token);
    }
    public async Task<BackupWorkspace> GetAsync(CancellationToken token = default)
    {
        var repositories = await Read<BackupRepository>("backup-repositories", token);
        var sets = await Read<BackupSet>("backup-sets", token);
        var scheduler = provider.GetRequiredService<IScheduledTaskService>();
        foreach (var set in sets.Where(set => set.ScheduleId.HasValue))
        {
            var editor = await scheduler.GetEditorAsync(set.ScheduleId, token);
            set.ScheduleEnabled = editor.Id.HasValue && editor.IsEnabled;
            set.ScheduleUsesDaily = editor.ScheduleMode == ScheduledTaskScheduleMode.Daily;
            set.ScheduleHour = editor.Hour ?? 2; set.ScheduleMinute = editor.Minute ?? 0;
            set.ScheduleSummary = editor.Id.HasValue ? ScheduledTaskCompiler.Compile(editor).ScheduleSummary : "Schedule no longer exists";
        }
        var mounts = await shares.ListCurrentMountsAsync(token);
        var managed = await shares.ListManagedRemoteMountsAsync(token);
        return new(repositories, sets, await Read<BackupOperation>("backup-history", token),
            mounts.Select(mount => mount.LocalMountPath).Distinct().Order().ToArray())
        { NetworkDestinations = BuildNetworkDestinations(mounts, managed) };
    }

    public static IReadOnlyList<BackupNetworkDestination> BuildNetworkDestinations(
        IReadOnlyList<CurrentSystemMount> mounts, IReadOnlyList<ManagedRemoteShareMount> managed)
    {
        var result = new Dictionary<string, BackupNetworkDestination>(StringComparer.Ordinal);
        foreach (var mount in mounts.Where(mount => mount.IsNetworkMount))
            result[mount.LocalMountPath] = new(mount.LocalMountPath, mount.SourcePath, true, mount.IsReadOnly, null);
        foreach (var mount in managed)
        {
            var current = mounts.FirstOrDefault(item => item.LocalMountPath == mount.LocalMountPath && item.IsNetworkMount);
            result[mount.LocalMountPath] = new(mount.LocalMountPath, mount.RemoteUncPath,
                mount.IsMounted && current is not null, current?.IsReadOnly ?? false, mount.Id);
        }
        return result.Values.OrderByDescending(item => item.IsMounted && !item.IsReadOnly)
            .ThenBy(item => item.Share, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task SaveRepositoryAsync(BackupRepository repository, string? password, bool initialize, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        string? newReference = null;
        try
        {
            if (string.IsNullOrWhiteSpace(repository.Name)) throw new InvalidOperationException("Name this repository.");
            ValidatePath(repository.Path);
            if (repository.Path.Trim('/') == "") throw new InvalidOperationException("Choose a dedicated backup folder, not the filesystem root (/).");
            var mounts = await shares.ListCurrentMountsAsync(token);
            var mount = mounts.Where(item => IsWithin(repository.Path, item.LocalMountPath)).OrderByDescending(item => item.LocalMountPath.Length).FirstOrDefault();
            if (mount?.IsReadOnly == true) throw new InvalidOperationException("This destination is mounted read-only.");
            repository.MountPath = mount?.LocalMountPath ?? ""; repository.MountSource = mount?.SourcePath ?? "";
            var repositories = await Read<BackupRepository>("backup-repositories", token);
            var existing = repositories.FirstOrDefault(item => item.Id == repository.Id);
            // Never trust a secret reference submitted by the caller.
            repository.PasswordReference = existing?.PasswordReference ?? "";
            if (password?.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidOperationException("Repository passwords must be a single line without control characters.");
            if (!string.IsNullOrEmpty(password)) repository.PasswordReference = newReference = await secrets.StoreSecretAsync(password, "Restic repository " + repository.Name, token);
            if (repository.PasswordReference.Length == 0) throw new InvalidOperationException("Enter the repository password. Keep a recovery copy somewhere safe.");
            if (initialize)
            {
                // restic itself refuses init over an existing repository. Never remove existing data.
                await Restic(repository, ["init"], token);
            }
            else await Restic(repository, ["snapshots", "--json"], token);
            repositories.RemoveAll(item => item.Id == repository.Id); repositories.Add(repository);
            await Write("backup-repositories", repositories, token);
            var storedReference = newReference; newReference = null;
            if (storedReference is not null && existing?.PasswordReference is { Length: > 0 } old) await secrets.DeleteSecretAsync(old, token);
        }
        catch { if (newReference is not null) await secrets.DeleteSecretAsync(newReference, CancellationToken.None); throw; }
        finally { Gate.Release(); }
    }

    public async Task SaveSetAsync(BackupSet set, bool schedule, int hour, int minute, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (string.IsNullOrWhiteSpace(set.Name)) throw new InvalidOperationException("Name this backup set.");
            var repo = await Repository(set.RepositoryId, token);
            foreach (var path in set.Sources)
            {
                ValidatePath(path);
                if (path == "/") throw new InvalidOperationException("Select specific directories instead of the entire filesystem.");
                if (IsWithin(repo.Path, path) || IsWithin(path, repo.Path)) throw new InvalidOperationException("Backup sources cannot contain the repository or be inside it.");
            }
            if (!set.IncludeLms && set.Sources.Count == 0) throw new InvalidOperationException("Select LMS data or at least one source directory.");
            if (set.KeepDaily < 0 || set.KeepWeekly < 0 || set.KeepMonthly < 0 || set.KeepDaily + set.KeepWeekly + set.KeepMonthly == 0)
                throw new InvalidOperationException("Keep at least one daily, weekly or monthly snapshot.");
            if (hour is < 0 or > 23 || minute is < 0 or > 59) throw new InvalidOperationException("Invalid schedule time.");
            var sets = await Read<BackupSet>("backup-sets", token);
            var old = sets.FirstOrDefault(item => item.Id == set.Id);
            set.ScheduleId = old?.ScheduleId;
            // Persist the set before enabling its schedule so the callback always has a target.
            sets.RemoveAll(item => item.Id == set.Id); sets.Add(set);
            await Write("backup-sets", sets, token);
            var scheduler = provider.GetRequiredService<IScheduledTaskService>();
            if (schedule || set.ScheduleId is not null)
            {
                var task = await scheduler.GetEditorAsync(set.ScheduleId, token);
                task.Name = "Backup: " + set.Name; task.Description = "Managed by System → Backup & Restore";
                task.TaskKind = ScheduledTaskKind.HostBackup; task.CommandText = set.Id.ToString(); task.RunAsUser = "root"; task.IsEnabled = schedule;
                // Preserve an advanced schedule edited in the existing Scheduling screen.
                if (task.Id is null || task.ScheduleMode == ScheduledTaskScheduleMode.Daily)
                { task.ScheduleMode = ScheduledTaskScheduleMode.Daily; task.Hour = hour; task.Minute = minute; }
                set.ScheduleId = await scheduler.SaveTaskAsync(task, token);
                set.ScheduleEnabled = schedule; set.ScheduleHour = task.Hour ?? hour; set.ScheduleMinute = task.Minute ?? minute;
                set.ScheduleSummary = ScheduledTaskCompiler.Compile(task).ScheduleSummary;
                await Write("backup-sets", sets, token);
            }
        }
        finally { Gate.Release(); }
    }

    public async Task RunBackupAsync(Guid setId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        var start = DateTimeOffset.UtcNow; Guid repositoryId = Guid.Empty; string? scratch = null;
        try
        {
            var set = (await Read<BackupSet>("backup-sets", token)).Single(item => item.Id == setId);
            var repository = await Repository(set.RepositoryId, token); repositoryId = repository.Id;
            var sources = set.Sources.ToList();
            if (set.IncludeLms)
            {
                // Stable staging path makes restored DB/keys identifiable without exposing a live DB copy.
                scratch = Path.Combine(DataDirectory, "backup-staging", set.Id.ToString("N"));
                Directory.CreateDirectory(scratch);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var sqlite = (SqliteConnection)database.Database.GetDbConnection();
                await database.Database.OpenConnectionAsync(token);
                var snapshotPath = Path.Combine(scratch, "linuxmadesane.db");
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath }.ToString()))
                { destination.Open(); sqlite.BackupDatabase(destination); }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshotPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var keysPath = hostPaths.ProtectionKeyDirectory;
                if (!Directory.Exists(keysPath)) throw new InvalidOperationException("The LMS protection key directory could not be found. A database backup without its keys would not recover saved credentials.");
                var keyFiles = Directory.GetFiles(keysPath, "key-*.xml");
                if (keyFiles.Length == 0) throw new InvalidOperationException("No LMS protection keys were found. The backup cannot claim credential recovery without them.");
                var keyCopy = Path.Combine(scratch, "protection-keys"); Directory.CreateDirectory(keyCopy);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyCopy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var path in keyFiles)
                { var target = Path.Combine(keyCopy, Path.GetFileName(path)); File.Copy(path, target, true); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" })
                {
                    var source = Path.Combine(hostPaths.ApplicationDirectory, file);
                    if (!File.Exists(source)) continue;
                    var target = Path.Combine(scratch, file); File.Copy(source, target, true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                await File.WriteAllTextAsync(Path.Combine(scratch, "restore-info.json"), JsonSerializer.Serialize(new { Database = "linuxmadesane.db", Keys = "protection-keys", CreatedUtc = DateTimeOffset.UtcNow }), token);
                sources.Add(scratch);
            }
            var args = new List<string> { "backup", "--json", "--tag", "lms-set-" + set.Id.ToString("N"), "--" };
            args.AddRange(sources);
            var output = await Restic(repository, args.ToArray(), token);
            // Retention is scoped to this set. Failed/partial backup never triggers forgetting.
            var retention = await Restic(repository, ["forget", "--tag", "lms-set-" + set.Id.ToString("N"), "--group-by", "host,tags",
                "--keep-daily", set.KeepDaily.ToString(), "--keep-weekly", set.KeepWeekly.ToString(), "--keep-monthly", set.KeepMonthly.ToString(), "--prune", "--json"], token);
            await History(repositoryId, setId, "Backup & retention", start, true, output + "\n" + retention, CancellationToken.None);
        }
        catch (Exception e) { await History(repositoryId, setId, "Backup", start, false, e.Message, CancellationToken.None); throw; }
        finally
        {
            try { if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            finally { Gate.Release(); }
        }
    }

    public async Task<IReadOnlyList<BackupSnapshot>> SnapshotsAsync(Guid repositoryId, CancellationToken token = default)
    {
        var output = await Restic(await Repository(repositoryId, token), ["snapshots", "--json"], token);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.EnumerateArray().Select(value => new BackupSnapshot(value.GetProperty("id").GetString()!,
            value.GetProperty("time").GetDateTimeOffset(), value.GetProperty("paths").EnumerateArray().Select(path => path.GetString()!).ToArray())).ToArray();
    }
    public async Task<IReadOnlyList<BackupFile>> FilesAsync(Guid repositoryId, string snapshot, CancellationToken token = default)
    {
        ValidateSnapshot(snapshot);
        var output = await Restic(await Repository(repositoryId, token), ["ls", "--json", snapshot], token);
        var result = new List<BackupFile>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line); var value = document.RootElement;
            if (!value.TryGetProperty("path", out var path)) continue;
            result.Add(new(path.GetString()!, value.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                value.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
        }
        return result;
    }
    public async Task<string> InspectRepositoryAsync(Guid repositoryId, bool check, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        var start = DateTimeOffset.UtcNow;
        try
        {
            var output = await Restic(await Repository(repositoryId, token), check ? ["check", "--read-data"] : ["stats", "--json"], token);
            await History(repositoryId, null, check ? "Integrity check" : "Statistics", start, true, output, CancellationToken.None); return output;
        }
        catch (Exception e) { await History(repositoryId, null, "Repository inspection", start, false, e.Message, CancellationToken.None); throw; }
        finally { Gate.Release(); }
    }

    public async Task RestoreAsync(Guid repositoryId, string snapshot, string target, IReadOnlyList<string> include, bool overwriteConfirmed, CancellationToken token = default)
    {
        ValidateSnapshot(snapshot); ValidatePath(target);
        if (target is "/" or "/etc" or "/usr" or "/var" or "/opt" || IsWithin(DataDirectory, target) || IsWithin(target, DataDirectory))
            throw new InvalidOperationException("Restore to a separate staging directory. Live LMS data and system roots cannot be overwritten from the running application.");
        var repo = await Repository(repositoryId, token);
        if (IsWithin(target, repo.Path) || IsWithin(repo.Path, target)) throw new InvalidOperationException("Restore destination must not overlap the repository.");
        var files = await FilesAsync(repositoryId, snapshot, token);
        foreach (var path in include)
            if (!files.Any(file => file.Path == path) || path.Split('/').Any(part => part is ".." or "."))
                throw new InvalidOperationException("Select existing snapshot files or directories; relative traversal is not allowed.");
        await Gate.WaitAsync(token); var start = DateTimeOffset.UtcNow;
        try
        {
            var validation = await runner.RunAsync(new("python3", ["-c", "import pathlib,sys; p=pathlib.Path(sys.argv[1]); parts=[p,*p.parents]; assert not any(x.is_symlink() for x in parts), 'Destination contains a symbolic link'; assert not p.exists() or p.is_dir(), 'Destination must be a directory'; assert not p.exists() or not any(x.is_symlink() for x in p.rglob('*')), 'Destination contains existing symbolic links'; assert sys.argv[2]=='yes' or not p.exists() or not any(p.iterdir()), 'Destination must be empty unless overwrite is explicitly confirmed'", target, overwriteConfirmed ? "yes" : "no"], true,
                TimeSpan.FromSeconds(15), "Validate restore destination"), false, token);
            if (validation.ExitCode != 0) throw new InvalidOperationException("Unsafe restore destination: " + validation.StandardError);
            var args = new List<string> { "restore", snapshot, "--target", target };
            foreach (var path in include) { args.Add("--include"); args.Add(EscapeRestorePattern(path)); }
            var output = await Restic(repo, args.ToArray(), token);
            await History(repositoryId, null, "Restore", start, true, output, CancellationToken.None);
        }
        catch (Exception e) { await History(repositoryId, null, "Restore", start, false, e.Message, CancellationToken.None); throw; }
        finally { Gate.Release(); }
    }

    public async Task<string> ValidateLmsRestoreAsync(string restoredDirectory, CancellationToken token = default)
    {
        ValidatePath(restoredDirectory);
        var result = await runner.RunAsync(new("python3", ["-c", "import pathlib,sqlite3,sys; p=pathlib.Path(sys.argv[1]); db=p/'linuxmadesane.db'; assert db.is_file(), 'Choose the restored LMS staging folder containing linuxmadesane.db'; c=sqlite3.connect('file:'+str(db)+'?mode=ro',uri=True); assert c.execute('pragma integrity_check').fetchone()[0]=='ok', 'Database integrity failed'; assert c.execute(\"select count(*) from sqlite_master where type='table' and name='protected_secrets'\").fetchone()[0]>0, 'Not an LMS database'; assert list((p/'protection-keys').glob('key-*.xml')), 'LMS protection keys missing'; print('Database integrity checked and protection keys present. Stop LMS before replacing its database and keys. Keep a copy of current data, retain service ownership and permissions, then start LMS and verify login and saved credentials. This validation has not changed live LMS data.')", restoredDirectory],
            true, TimeSpan.FromSeconds(30), "Validate staged LMS restore"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError); return result.StandardOutput;
    }
    private async Task<BackupRepository> Repository(Guid id, CancellationToken token) =>
        (await Read<BackupRepository>("backup-repositories", token)).Single(item => item.Id == id);

    public static string EscapeRestorePattern(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("?", "\\?", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);
    private async Task<string> Restic(BackupRepository repository, string[] arguments, CancellationToken token)
    {
        if (repository.MountPath.Length > 0)
        {
            var mounts = await shares.ListCurrentMountsAsync(token);
            if (!mounts.Any(mount => mount.LocalMountPath == repository.MountPath && mount.SourcePath == repository.MountSource && !mount.IsReadOnly))
                throw new InvalidOperationException("The repository's original destination mount is unavailable or changed. Reconnect it before continuing; LMS will not write into the empty mount directory.");
        }
        var password = await secrets.ResolveSecretAsync(repository.PasswordReference, token) ?? throw new InvalidOperationException("The repository password cannot be resolved.");
        var path = Path.Combine(Path.GetTempPath(), "lms-restic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var canonical = await runner.RunAsync(new("python3", ["-c", "import pathlib,sys; paths=[pathlib.Path(x).resolve() for x in sys.argv[1:]]; repo=paths[0]; assert all(repo!=p and repo not in p.parents and p not in repo.parents for p in paths[1:]), 'Source and repository resolve to overlapping directories'", repository.Path, ..arguments.SkipWhile(value => value != "--").Skip(1)],
                true, TimeSpan.FromSeconds(15), "Validate backup repository path"), false, token);
            if (canonical.ExitCode != 0) throw new InvalidOperationException("Unsafe repository/source paths: " + canonical.StandardError);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(path, options))
            { var bytes = System.Text.Encoding.UTF8.GetBytes(password); await file.WriteAsync(bytes, token); }
            var result = await runner.RunAsync(new("restic", ["--repo", repository.Path, "--password-file", path, "--no-cache", ..arguments],
                true, TimeSpan.FromHours(12), "Restic " + arguments[0]), false, token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Restic {arguments[0]} failed (exit {result.ExitCode}): {result.StandardError} {result.StandardOutput}");
            return result.StandardOutput;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private async Task History(Guid repository, Guid? set, string kind, DateTimeOffset start, bool success, string detail, CancellationToken token)
    {
        var history = await Read<BackupOperation>("backup-history", token);
        history.Insert(0, new(Guid.NewGuid(), repository, set, kind, start, DateTimeOffset.UtcNow, success, detail.Length > 65536 ? detail[^65536..] : detail));
        await Write("backup-history", history.Take(100).ToArray(), token);
    }
    public bool CanHandle(ScheduledTaskDefinition task) => task.TaskKind == ScheduledTaskKind.HostBackup;
    public async Task<ScheduledTaskRunResult> ExecuteAsync(ScheduledTaskDefinition task, CancellationToken token)
    {
        try { await RunBackupAsync(Guid.Parse(task.CommandText), token); return new(true, "Backup completed: " + task.Name); }
        catch (Exception e) { return new(false, "Backup failed: " + e.Message); }
    }
    public static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('/').Any(part => part is "." or "..") || path.IndexOfAny(['\0', '\n', '\r']) >= 0)
            throw new InvalidOperationException("Use an absolute filesystem path without traversal or control characters.");
    }
    private static void ValidateSnapshot(string value)
    { if (!Regex.IsMatch(value, "^[a-f0-9]{8,64}$")) throw new InvalidOperationException("Select an explicit snapshot ID."); }
    private static bool IsWithin(string path, string parent) => Path.GetFullPath(path).TrimEnd('/') == Path.GetFullPath(parent).TrimEnd('/') ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd('/') + "/", StringComparison.Ordinal);
}
