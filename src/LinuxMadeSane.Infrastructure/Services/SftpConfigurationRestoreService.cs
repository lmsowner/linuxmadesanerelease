// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SftpConfigurationRestoreService(ISftpServerStore store, ISftpBackupService backups,
    ILinuxCommandRunner runner, ISftpAuditService audit) : ISftpConfigurationRestoreService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<SftpApplyResult> RestoreAsync(Guid snapshotId, bool approved, CancellationToken cancellationToken = default)
    {
        SftpBackupSnapshot? safety = null;
        var logs = new List<OperationLogEntry>();
        var changed = false;
        var recovered = false;
        try
        {
            if (!approved) throw new InvalidOperationException("Confirm Restore configuration to apply this snapshot.");
            var snapshot = await store.GetBackupSnapshotAsync(snapshotId, cancellationToken)
                ?? throw new InvalidOperationException("This SFTP snapshot no longer exists.");
            if (!snapshot.RollbackAvailable || snapshot.Files.Count == 0) throw new InvalidOperationException("This snapshot cannot be restored.");
            foreach (var file in snapshot.Files)
            {
                if (file.SourcePath != SftpServerDefaults.MainSshdConfigPath && file.SourcePath != SftpServerDefaults.ManagedDropInPath)
                    throw new InvalidOperationException("Only the SFTP SSH configuration files can be restored here. User files and other paths are never restored or removed.");
                if (File.Exists(file.SourcePath) && new FileInfo(file.SourcePath).LinkTarget is not null)
                    throw new InvalidOperationException("The current SSH configuration is a symbolic link. Restore it manually after reviewing the destination.");
                if (file.ExistedBeforeSnapshot && (!File.Exists(file.BackupPath)
                    || new FileInfo(file.BackupPath).LinkTarget is not null
                    || !Path.GetFullPath(file.BackupPath).StartsWith(Path.GetFullPath(snapshot.StorageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    throw new InvalidOperationException("A backup file is missing or outside this snapshot. No SSH configuration was changed.");
            }
            safety = await backups.CreateSnapshotAsync("Before restoring SFTP configuration", snapshot.Files.Select(x => x.SourcePath).ToArray(), cancellationToken);
            changed = true;
            await backups.RestoreAsync(snapshot, cancellationToken);
            var validation = await RunAsync("if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi", "Validate restored SSH configuration", cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(validation, "Validate restored SSH configuration"));
            if (validation.ExitCode != 0) throw new InvalidOperationException("The restored SSH configuration failed validation: " + SftpSystemCommandHelper.BuildFailureDetail(validation));
            var reload = await RunAsync("systemctl reload sshd || systemctl reload ssh", "Reload validated SSH configuration", cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(reload, "Reload SSH service"));
            if (reload.ExitCode != 0) throw new InvalidOperationException("SSH could not reload the restored configuration: " + SftpSystemCommandHelper.BuildFailureDetail(reload));
            await audit.RecordAsync(new(Guid.NewGuid(), "host.restore", "host", "local", "Restored SFTP SSH configuration.", $"Snapshot: {snapshotId}. User accounts, passwords and uploaded files were not restored.", true, DateTimeOffset.UtcNow, safety.Id), cancellationToken);
            return Result(true, "SSH configuration restored. User accounts, passwords and uploaded files were not changed.");
        }
        catch (Exception exception)
        {
            var summary = exception.Message;
            if (changed && safety is not null)
            {
                try
                {
                    await backups.RestoreAsync(safety, CancellationToken.None);
                    var check = await RunAsync("if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi", "Validate recovered SSH configuration", CancellationToken.None);
                    if (check.ExitCode != 0) throw new InvalidOperationException(SftpSystemCommandHelper.BuildFailureDetail(check));
                    var reload = await RunAsync("systemctl reload sshd || systemctl reload ssh", "Reload recovered SSH configuration", CancellationToken.None);
                    if (reload.ExitCode != 0) throw new InvalidOperationException(SftpSystemCommandHelper.BuildFailureDetail(reload));
                    recovered = true; summary += " The previous configuration was recovered.";
                }
                catch (Exception recovery) { summary += " Automatic recovery could not finish: " + recovery.Message + $". Recovery snapshot: {safety.Id}."; }
            }
            await audit.RecordAsync(new(Guid.NewGuid(), "host.restore", "host", "local", summary, $"Snapshot: {snapshotId}", false, DateTimeOffset.UtcNow, safety?.Id), CancellationToken.None);
            return Result(false, summary);
        }

        SftpApplyResult Result(bool success, string summary) => new(success, summary, logs,
            new(success, summary, success ? [] : [summary], [], "sshd -t"), safety, recovered, []);
    }

    private Task<LinuxCommandResult> RunAsync(string command, string description, CancellationToken token) =>
        runner.RunAsync(new("bash", ["-lc", command], true, Timeout, description), false, token);
}
