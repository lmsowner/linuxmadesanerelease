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
            set.ScheduleMode = editor.ScheduleMode;
            set.ScheduleDaysOfWeekCsv = editor.DaysOfWeekCsv;
            set.ScheduleDayOfMonth = editor.DayOfMonth ?? 1;
            set.ScheduleHour = editor.Hour ?? 2; set.ScheduleMinute = editor.Minute ?? 0;
            set.ScheduleSummary = editor.Id.HasValue ? ScheduledTaskCompiler.Compile(editor).ScheduleSummary : "Schedule no longer exists";
        }
        var mounts = await shares.ListCurrentMountsAsync(token);
        var managed = await shares.ListManagedRemoteMountsAsync(token);
        return new(repositories, sets, await Read<BackupOperation>("backup-history", token),
            mounts.Select(mount => mount.LocalMountPath).Distinct().Order().ToArray())
        { SupportsPasswordFree = await SupportsPasswordFree(token), NetworkDestinations = BuildNetworkDestinations(mounts, managed), DataDirectory = DataDirectory,
            ApplicationDirectory = hostPaths.ApplicationDirectory, ProtectionKeyDirectory = hostPaths.ProtectionKeyDirectory };
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
        bool directoryCreated = false;
        try
        {
            if (string.IsNullOrWhiteSpace(repository.Name)) throw new InvalidOperationException("Name this repository.");
            ValidatePath(repository.Path);
            if (repository.Path.Trim('/') == "") throw new InvalidOperationException("Choose a dedicated backup folder, not the filesystem root (/).");
            var mounts = await shares.ListCurrentMountsAsync(token);
            var mount = mounts.Where(item => IsWithin(repository.Path, item.LocalMountPath)).OrderByDescending(item => item.LocalMountPath.Length).FirstOrDefault();
            if (repository.RequiredNetworkMountPath.Length > 0 &&
                (mount is not { IsNetworkMount: true } || mount.LocalMountPath != repository.RequiredNetworkMountPath))
                throw new InvalidOperationException("This network share is no longer connected. Reconnect it before creating backup storage.");
            if (mount?.IsReadOnly == true) throw new InvalidOperationException("This destination is mounted read-only.");
            repository.MountPath = mount?.LocalMountPath ?? ""; repository.MountSource = mount?.SourcePath ?? "";
            var repositories = await Read<BackupRepository>("backup-repositories", token);
            var existing = repositories.FirstOrDefault(item => item.Id == repository.Id);
            // Existing repository key protection cannot be changed by editing its settings.
            if (existing is not null) repository.NoPassword = existing.NoPassword;
            // Never trust a secret reference submitted by the caller.
            repository.PasswordReference = existing?.PasswordReference ?? "";
            if (password?.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidOperationException("Repository passwords must be a single line without control characters.");
            if (repository.NoPassword && !await SupportsPasswordFree(token))
                throw new InvalidOperationException("This host's installed restic requires a backup password. Select Protect backups with a password, or upgrade restic to 0.17 or newer for password-free backups. No backup storage was created.");
            if (!repository.NoPassword && initialize && (string.IsNullOrWhiteSpace(password) || password.Length < 12)) throw new InvalidOperationException("Choose a backup encryption password with at least 12 characters. Recovery requires this password.");
            if (!repository.NoPassword && !string.IsNullOrEmpty(password)) repository.PasswordReference = newReference = await secrets.StoreSecretAsync(password, "Restic repository " + repository.Name, token);
            if (!repository.NoPassword && repository.PasswordReference.Length == 0) throw new InvalidOperationException("Enter the repository password. Keep a recovery copy somewhere safe.");
            if (repository.CreateNewDirectory)
            {
                if (!initialize || existing is not null)
                    throw new InvalidOperationException("Separate subfolders are only created for new backup storage.");
                var parent = await runner.RunAsync(new("mkdir", ["-p", "--", Path.GetDirectoryName(repository.Path)!], true,
                    TimeSpan.FromSeconds(15), "Create backup parent folder"), false, token);
                if (parent.ExitCode != 0) throw new InvalidOperationException("Could not create the backup parent folder: " + parent.StandardError);
                // Exclusive mkdir is atomic even when another LMS host uses this share.
                var created = await runner.RunAsync(new("mkdir", ["-m", "700", "--", repository.Path], true,
                    TimeSpan.FromSeconds(15), "Create separate host backup folder without reusing existing data"), false, token);
                if (created.ExitCode != 0)
                    throw new InvalidOperationException($"Could not create {repository.Path}. If this folder already exists, edit the backup subfolder name. Existing data has not been changed. " + created.StandardError);
                directoryCreated = true;
            }
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
        catch
        {
            if (newReference is not null) await secrets.DeleteSecretAsync(newReference, CancellationToken.None);
            if (directoryCreated)
            {
                // Remove only an empty directory created by this attempt. Never remove
                // partial repository data or anything another process has written.
                try { await runner.RunAsync(new("rmdir", ["--", repository.Path], true, TimeSpan.FromSeconds(5), "Remove empty backup folder after failed creation"), false, CancellationToken.None); }
                catch { /* Preserve the original failure and any remaining data. */ }
            }
            throw;
        }
        finally { Gate.Release(); }
    }

    public async Task<string> InspectFullSystemAsync(Guid repositoryId, CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Full-system recovery requires a Linux host.");
        var repo = await Repository(repositoryId, token);
        var result = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.FullSystemScript, "--repository", repo.Path], true,
            TimeSpan.FromSeconds(30), "Check full-system recovery disks and backup destination"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        using var plan = JsonDocument.Parse(result.StandardOutput);
        return "Local filesystems: " + string.Join(", ", plan.RootElement.GetProperty("sources").EnumerateArray().Select(x => x.GetString())) +
            "\nExcluded: " + string.Join(", ", plan.RootElement.GetProperty("excludes").EnumerateArray().Select(x => x.GetString())) +
            "\n" + plan.RootElement.GetProperty("warning").GetString();
    }

    public async Task SaveSetAsync(BackupSet set, bool schedule, int hour, int minute, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (string.IsNullOrWhiteSpace(set.Name)) throw new InvalidOperationException("Name this backup set.");
            if (set.Notes.Length > 8000) throw new InvalidOperationException("Keep backup notes within 8,000 characters.");
            var repo = await Repository(set.RepositoryId, token);
            if (set.FullSystem)
            {
                var packageStatus = await provider.GetRequiredService<IInfrastructureDiagnosticsService>().GetPackageStatusAsync("FullBackup", token);
                if (!packageStatus.Installed) throw new InvalidOperationException("Select Install & Configure for full-system recovery first. Required: " + string.Join(", ", packageStatus.Packages));
                await InspectFullSystemAsync(set.RepositoryId, token);
                set.IncludeLms = true; set.Sources = [];
            }
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
            set.LastFinishedUtc = old?.LastFinishedUtc;
            set.LastRunSucceeded = old?.LastRunSucceeded;
            set.LastSuccessfulBackupUtc = old?.LastSuccessfulBackupUtc;
            set.RecoveryTestRecordedUtc = old?.RecoveryTestRecordedUtc;
            set.RecoveryTestSnapshotId = old?.RecoveryTestSnapshotId ?? "";
            set.RecoveryTestNotes = old?.RecoveryTestNotes ?? "";
            var scheduler = provider.GetRequiredService<IScheduledTaskService>();
            if (schedule || set.ScheduleId is not null)
            {
                var task = await scheduler.GetEditorAsync(set.ScheduleId, token);
                task.Name = "Backup: " + set.Name; task.Description = set.Notes;
                task.TaskKind = ScheduledTaskKind.HostBackup; task.CommandText = set.Id.ToString(); task.RunAsUser = "root"; task.IsEnabled = schedule;
                // Preserve an advanced schedule edited in the existing Scheduling screen.
                if (set.ScheduleMode is ScheduledTaskScheduleMode.Hourly or ScheduledTaskScheduleMode.Daily or ScheduledTaskScheduleMode.Weekly or ScheduledTaskScheduleMode.Monthly)
                {
                    task.ScheduleMode = set.ScheduleMode; task.Hour = hour; task.Minute = minute;
                    task.DaysOfWeekCsv = set.ScheduleDaysOfWeekCsv; task.DayOfMonth = set.ScheduleDayOfMonth;
                }
                ScheduledTaskCompiler.ValidateAndThrow(task);
                // Validate first, then persist before enabling so the callback has a target.
                sets.RemoveAll(item => item.Id == set.Id); sets.Add(set);
                await Write("backup-sets", sets, token);
                set.ScheduleId = await scheduler.SaveTaskAsync(task, token);
                set.ScheduleEnabled = schedule; set.ScheduleHour = task.Hour ?? hour; set.ScheduleMinute = task.Minute ?? minute;
                set.ScheduleSummary = ScheduledTaskCompiler.Compile(task).ScheduleSummary;
                await Write("backup-sets", sets, token);
            }
            else
            {
                sets.RemoveAll(item => item.Id == set.Id); sets.Add(set);
                await Write("backup-sets", sets, token);
            }
            await History(repo.Id, set.Id, old is null ? "Plan created" : "Plan updated", DateTimeOffset.UtcNow, true,
                $"Sources: {(set.FullSystem ? "All supported local filesystems and ReaR boot recovery; " : set.IncludeLms ? "LMS configuration; " : "")}{string.Join("; ", set.Sources)}\nDestination: {repo.Name} ({repo.Path})\nScheduled backups: {(schedule ? "Enabled" : "Disabled")}", token);
        }
        finally { Gate.Release(); }
    }

    public async Task RecordRecoveryTestAsync(Guid setId, string snapshotId, string evidence, CancellationToken token = default)
    {
        ValidateSnapshot(snapshotId);
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length < 20 || evidence.Length > 8000)
            throw new InvalidOperationException("Record the test machine, successful Linux boot, LMS login and credential/workload checks (20–8,000 characters). Never include passwords.");
        await Gate.WaitAsync(token);
        try
        {
            var sets = await Read<BackupSet>("backup-sets", token);
            var set = sets.Single(item => item.Id == setId);
            if (!set.FullSystem) throw new InvalidOperationException("Select a full-system backup plan.");
            var snapshot = (await SnapshotsAsync(set.RepositoryId, token)).SingleOrDefault(item => item.Id == snapshotId && item.Tags.Contains("lms-full-system") && item.Tags.Contains("lms-backup-complete") && item.Tags.Contains("lms-set-" + setId.ToString("N")))
                ?? throw new InvalidOperationException("Choose a completed full-system snapshot belonging to this plan.");
            set.RecoveryTestSnapshotId = snapshot.Id; set.RecoveryTestRecordedUtc = DateTimeOffset.UtcNow; set.RecoveryTestNotes = evidence.Trim();
            await Write("backup-sets", sets, token);
            await History(set.RepositoryId, set.Id, "Recovery test recorded", DateTimeOffset.UtcNow, true,
                "User-reported successful recovery drill. Snapshot: " + snapshot.Id + "\n" + evidence.Trim(), token);
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
            if (set.FullSystem)
            {
                // Existing plans may predate newly identified recovery dependencies.
                // Install missing packages at use, before producing any recovery media.
                await provider.GetRequiredService<IInfrastructureDiagnosticsService>().InstallPackagesAsync("FullBackup", token);
            }
            var sources = set.Sources.ToList();
            var stagingRoot = OperatingSystem.IsLinux()
                ? set.FullSystem ? Path.Combine(DataDirectory, "backup-staging") : "/dev/shm"
                : Path.GetTempPath();
            Directory.CreateDirectory(stagingRoot);
            if (set.FullSystem && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(stagingRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            scratch = Path.Combine(stagingRoot, "lms-backup-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (set.FullSystem && OperatingSystem.IsLinux())
            {
                // Reuse the selected backup mount for bulky recovery build data.
                // An encrypted loop filesystem supplies POSIX semantics even on SMB.
                var prepared = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.FullSystemScript,
                    "--repository", repository.Path, "--bundle", scratch, "--prepare-workspace"], true,
                    TimeSpan.FromMinutes(5), "Prepare encrypted recovery workspace on the selected backup destination"), false, token);
                if (prepared.ExitCode != 0) throw RecoveryMediaFailure(prepared.StandardError);
            }
            // This recovery record travels with the encrypted snapshot, independently of the live LMS database.
            await File.WriteAllTextAsync(Path.Combine(scratch, "backup-plan.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Host = Environment.MachineName, CreatedUtc = start,
                PlanId = set.Id, set.Name, set.Notes, set.FullSystem, set.IncludeLms, set.Sources,
                DestinationName = repository.Name, DestinationPath = repository.Path,
                set.ScheduleSummary, set.ScheduleEnabled, set.KeepDaily, set.KeepWeekly, set.KeepMonthly,
                Recovery = set.FullSystem ? "Full system with ReaR boot recovery media. See FULL-SYSTEM-RECOVERY.txt. Restore drill not recorded." : "Restore to an alternate directory. This is a file/configuration backup, not bootable bare-metal recovery."
            }), token);
            sources.Add(scratch);
            if (set.IncludeLms)
            {
                // Stable staging path makes restored DB/keys identifiable without exposing a live DB copy.
                var sqlite = (SqliteConnection)database.Database.GetDbConnection();
                await database.Database.OpenConnectionAsync(token);
                var snapshotPath = Path.Combine(scratch, "linuxmadesane.db");
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath }.ToString()))
                {
                    destination.Open(); sqlite.BackupDatabase(destination);
                    using var standalone = destination.CreateCommand();
                    standalone.CommandText = "PRAGMA journal_mode=DELETE"; standalone.ExecuteNonQuery();
                }
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
                await LmsConfigurationRecovery.WriteRecoveryFilesAsync(scratch, token);
                var specificationPath = Path.Combine(scratch, "recovery-specification.json");
                await File.WriteAllTextAsync(specificationPath, JsonSerializer.Serialize(LmsConfigurationRecovery.Specification(provider, hostPaths, DataDirectory,
                    Path.GetFileName(new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource))), token);
                if (set.FullSystem)
                {
                    var media = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.FullSystemScript, "--repository", repository.Path, "--bundle", scratch], true,
                        TimeSpan.FromMinutes(35), "Create ReaR boot recovery media; no disks will be formatted"), false, token);
                    if (media.ExitCode != 0) throw RecoveryMediaFailure(media.StandardError);
                    using var full = JsonDocument.Parse(media.StandardOutput);
                    sources.AddRange(full.RootElement.GetProperty("sources").EnumerateArray().Select(item => item.GetString()!));
                }
                var captured = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.Script, "--bundle", scratch, "--collect", specificationPath], true,
                    TimeSpan.FromMinutes(10), "Capture and validate LMS configuration recovery bundle"), false, token);
                if (captured.ExitCode != 0) throw new InvalidOperationException("Configuration recovery capture failed: " + captured.StandardError);

            }
            var args = new List<string> { "backup", "--json", "--tag", "lms-set-" + set.Id.ToString("N") };
            if (set.FullSystem)
            {
                args.AddRange(["--tag", "lms-full-system", "--one-file-system"]);
                using var full = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "full-system-recovery.json"), token));
                // Explicitly include the separately mounted recovery bundle. Root traversal
                // skips virtual mounts through --one-file-system instead of excluding that bundle.
                foreach (var excluded in full.RootElement.GetProperty("excludes").EnumerateArray().Select(item => item.GetString()!).Where(path => !IsWithin(scratch, path)))
                    args.AddRange(["--exclude", EscapeRestorePattern(excluded)]);
                var dbFile = Path.GetFullPath(new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource);
                foreach (var path in new[] { dbFile, dbFile + "-wal", dbFile + "-shm" }) args.AddRange(["--exclude", EscapeRestorePattern(path)]);
            }
            args.Add("--");
            args.AddRange(sources);
            var output = await Restic(repository, args.ToArray(), token, set.FullSystem);
            if (set.FullSystem)
                output += "\nCompletion marker:\n" + await Restic(repository, ["tag", "--add", "lms-backup-complete", "--path", scratch,
                    "--tag", "lms-full-system,lms-set-" + set.Id.ToString("N"), "--json"], token);
            // Retention is scoped to this set. Failed/partial backup never triggers forgetting.
            var retention = await Restic(repository, ["forget", "--tag", "lms-set-" + set.Id.ToString("N"), "--group-by", "host,tags",
                "--keep-daily", set.KeepDaily.ToString(), "--keep-weekly", set.KeepWeekly.ToString(), "--keep-monthly", set.KeepMonthly.ToString(), "--prune", "--json"], token);
            await History(repositoryId, setId, "Backup & retention", start, true, output + "\n" + retention, CancellationToken.None);
        }
        catch (Exception e) { await History(repositoryId, setId, "Backup", start, false, e.InnerException is null ? e.Message : e.Message + "\n\nBuild details:\n" + e.InnerException.Message, CancellationToken.None); throw; }
        finally
        {
            try
            {
                if (scratch is not null) await CleanupStagingAsync(scratch);
            }
            catch (Exception cleanupError)
            {
                // Cleanup must not hide a capture/backup failure or mark a completed backup as failed.
                try { await History(repositoryId, setId, "Temporary backup cleanup", DateTimeOffset.UtcNow, false,
                    "Backup staging cleanup failed: " + cleanupError.Message, CancellationToken.None); }
                catch { /* Retain the original operation result even if logging is unavailable. */ }
            }
            finally { Gate.Release(); }
        }
    }

    internal static InvalidOperationException RecoveryMediaFailure(string detail)
    {
        var message = detail.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
            ? "Boot recovery build ran out of temporary disk space. Free space on the selected backup destination and retry. No full-system backup was saved. See History & logs for the full build output."
            : detail.StartsWith("Not enough temporary space", StringComparison.Ordinal) || detail.StartsWith("The local disk needs", StringComparison.Ordinal)
                ? detail.Trim()
                : "Boot recovery media could not be created. No full-system backup was saved. See History & logs for the full build output.";
        return new InvalidOperationException(message, new InvalidOperationException(detail));
    }

    internal async Task CleanupStagingAsync(string scratch)
    {
        var stagingRoot = OperatingSystem.IsLinux() ? "/dev/shm" : Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if ((Path.GetDirectoryName(scratch) != stagingRoot && Path.GetDirectoryName(scratch) != Path.Combine(DataDirectory, "backup-staging")) ||
            !Regex.IsMatch(Path.GetFileName(scratch), "^lms-backup-staging-[0-9a-f]{32}$"))
            throw new InvalidOperationException("Refusing cleanup outside an LMS backup staging directory.");
        if (OperatingSystem.IsLinux())
        {
            // ReaR may leave root-owned private files even when media creation fails.
            var result = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.BackupCleanupScript, scratch, Path.GetDirectoryName(scratch)!], true,
                TimeSpan.FromMinutes(2), "Remove temporary LMS backup staging files"), false, CancellationToken.None);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        }
        else if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }

    public async Task<IReadOnlyList<BackupSnapshot>> SnapshotsAsync(Guid repositoryId, CancellationToken token = default)
    {
        var output = await Restic(await Repository(repositoryId, token), ["snapshots", "--json"], token);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.EnumerateArray().Select(value => new BackupSnapshot(value.GetProperty("id").GetString()!,
                value.GetProperty("time").GetDateTimeOffset(), value.GetProperty("paths").EnumerateArray().Select(path => path.GetString()!).ToArray())
                { Tags = value.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(tag => tag.GetString()!).ToArray() : [] }).ToArray();
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
            var restoredSnapshot = (await SnapshotsAsync(repositoryId, token)).FirstOrDefault(item => item.Id.StartsWith(snapshot, StringComparison.Ordinal));
            var planTags = restoredSnapshot?.Tags.Where(tag => tag.StartsWith("lms-set-", StringComparison.Ordinal)).ToArray() ?? [];
            Guid? restoredSetId = planTags.Length == 1 && Guid.TryParse(planTags[0][8..], out var parsedId) ? parsedId : null;
        await Gate.WaitAsync(token); var start = DateTimeOffset.UtcNow;
        try
        {
            var validation = await runner.RunAsync(new("python3", ["-c", "import pathlib,sys; p=pathlib.Path(sys.argv[1]); parts=[p,*p.parents]; assert not any(x.is_symlink() for x in parts), 'Destination contains a symbolic link'; assert not p.exists() or p.is_dir(), 'Destination must be a directory'; assert not p.exists() or not any(x.is_symlink() for x in p.rglob('*')), 'Destination contains existing symbolic links'; assert sys.argv[2]=='yes' or not p.exists() or not any(p.iterdir()), 'Destination must be empty unless overwrite is explicitly confirmed'", target, overwriteConfirmed ? "yes" : "no"], true,
                TimeSpan.FromSeconds(15), "Validate restore destination"), false, token);
            if (validation.ExitCode != 0) throw new InvalidOperationException("Unsafe restore destination: " + validation.StandardError);
            var args = new List<string> { "restore", snapshot, "--target", target };
            foreach (var path in include) { args.Add("--include"); args.Add(EscapeRestorePattern(path)); }
            var output = await Restic(repo, args.ToArray(), token);
            await History(repositoryId, restoredSetId, "Restore", start, true, output, CancellationToken.None);
        }
        catch (Exception e) { await History(repositoryId, restoredSetId, "Restore", start, false, e.Message, CancellationToken.None); throw; }
        finally { Gate.Release(); }
    }

    public async Task<string> ValidateLmsRestoreAsync(string restoredDirectory, CancellationToken token = default)
    {
        ValidatePath(restoredDirectory);
        var result = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.Script, "--bundle", restoredDirectory],
            true, TimeSpan.FromMinutes(2), "Validate complete staged LMS configuration recovery"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError); return result.StandardOutput;
    }
    public async Task<string> PrepareLmsRestoreAsync(string restoredDirectory, string preparationDirectory,
        string targetDataDirectory, string targetApplicationDirectory, string targetKeyDirectory, CancellationToken token = default)
    {
        foreach (var path in new[] { restoredDirectory, preparationDirectory, targetDataDirectory, targetApplicationDirectory, targetKeyDirectory }) ValidatePath(path);
        if (IsWithin(preparationDirectory, restoredDirectory) || IsWithin(restoredDirectory, preparationDirectory) ||
            IsWithin(preparationDirectory, DataDirectory) || IsWithin(DataDirectory, preparationDirectory) ||
            IsWithin(preparationDirectory, hostPaths.ApplicationDirectory) || IsWithin(hostPaths.ApplicationDirectory, preparationDirectory))
            throw new InvalidOperationException("Prepare recovery in a new private folder outside the bundle and live LMS directories.");
        var result = await runner.RunAsync(new("python3", ["-c", LmsConfigurationRecovery.Script, "--bundle", restoredDirectory,
            "--prepare", preparationDirectory, "--data-root", targetDataDirectory, "--app-root", targetApplicationDirectory, "--keys-root", targetKeyDirectory, "--database-file-name", Path.GetFileName(new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource)],
            true, TimeSpan.FromMinutes(5), "Prepare replacement-host LMS configuration recovery"), false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
        return result.StandardOutput + "\nReview " + preparationDirectory + "/restore-plan.json and RECOVERY.txt in the restored bundle. Live files and services have not been changed.";
    }

    private async Task<BackupRepository> Repository(Guid id, CancellationToken token) =>
        (await Read<BackupRepository>("backup-repositories", token)).Single(item => item.Id == id);

    public static string EscapeRestorePattern(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("?", "\\?", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);
    private async Task<bool> SupportsPasswordFree(CancellationToken token)
    {
        var help = await runner.RunAsync(new(InfrastructureDiagnosticsService.ResticExecutable, ["help"], false, TimeSpan.FromSeconds(10), "Check restic password-free support"), false, token);
        return help.ExitCode == 0 && (help.StandardOutput + help.StandardError).Contains("--insecure-no-password", StringComparison.Ordinal);
    }
    private async Task<string> Restic(BackupRepository repository, string[] arguments, CancellationToken token, bool fullSystem = false)
    {
        if (repository.MountPath.Length > 0)
        {
            var mounts = await shares.ListCurrentMountsAsync(token);
            if (!mounts.Any(mount => mount.LocalMountPath == repository.MountPath && mount.SourcePath == repository.MountSource && !mount.IsReadOnly))
                throw new InvalidOperationException("The repository's original destination mount is unavailable or changed. Reconnect it before continuing; LMS will not write into the empty mount directory.");
        }
        var password = repository.NoPassword ? "" : await secrets.ResolveSecretAsync(repository.PasswordReference, token) ?? throw new InvalidOperationException("The repository password cannot be resolved.");
        if (!repository.NoPassword && string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("An encryption password is required to open this backup repository.");
        var path = Path.Combine(OperatingSystem.IsLinux() ? "/dev/shm" : Path.GetTempPath(), "lms-restic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var canonical = await runner.RunAsync(new("python3", ["-c", "import pathlib,sys; full=sys.argv[1]=='full'; paths=[pathlib.Path(x).resolve() for x in sys.argv[2:]]; repo=paths[0]; assert all(repo!=p and repo not in p.parents and (full or p not in repo.parents) for p in paths[1:]), 'Source and repository resolve to overlapping directories'", fullSystem ? "full" : "files", repository.Path, ..arguments.SkipWhile(value => value != "--").Skip(1)],
                true, TimeSpan.FromSeconds(15), "Validate backup repository path"), false, token);
            if (canonical.ExitCode != 0) throw new InvalidOperationException("Unsafe repository/source paths: " + canonical.StandardError);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(path, options))
            { var bytes = System.Text.Encoding.UTF8.GetBytes(password); await file.WriteAsync(bytes, token); }
            string[] passwordOptions = [];
            if (repository.NoPassword)
            {
                if (!await SupportsPasswordFree(token))
                    throw new InvalidOperationException("This password-free backup requires restic 0.17 or newer. Upgrade restic on this host before opening it.");
                passwordOptions = ["--insecure-no-password"];
            }
            var result = await runner.RunAsync(new(InfrastructureDiagnosticsService.ResticExecutable, ["--repo", repository.Path, "--password-file", path, "--no-cache", ..passwordOptions, ..arguments],
                true, TimeSpan.FromHours(12), "Restic " + arguments[0]), false, token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Restic {arguments[0]} failed (exit {result.ExitCode}): {result.StandardError} {result.StandardOutput}");
            return result.StandardOutput;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private async Task History(Guid repository, Guid? set, string kind, DateTimeOffset start, bool success, string detail, CancellationToken token)
    {
        var history = await Read<BackupOperation>("backup-history", token);
        var plans = set.HasValue ? await Read<BackupSet>("backup-sets", token) : [];
        var plan = plans.FirstOrDefault(item => item.Id == set);
        if (plan is not null) detail = $"Plan: {plan.Name}\nNotes: {plan.Notes}\nSchedule: {plan.ScheduleSummary}\nRetention: {plan.KeepDaily} daily, {plan.KeepWeekly} weekly, {plan.KeepMonthly} monthly\n\n" + detail;
        if (plan is not null && kind.StartsWith("Backup", StringComparison.Ordinal))
        {
            plan.LastFinishedUtc = DateTimeOffset.UtcNow;
            plan.LastRunSucceeded = success;
            if (success) plan.LastSuccessfulBackupUtc = plan.LastFinishedUtc;
            await Write("backup-sets", plans, token);
        }
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
