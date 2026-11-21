// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SftpPermissionPolicyService(ILinuxCommandRunner runner)
{
    public const string GuardPath = "/usr/local/libexec/lms-sftp-guard";
    private static string SetupScript => Path.Combine(AppContext.BaseDirectory, "sftp-policy", "setup.sh");

    public async Task PrepareAsync(SftpManagedUser user, CancellationToken token)
    {
        if (user.Permissions == SftpFilePermissions.All) return;
        if (!OperatingSystem.IsLinux() || !File.Exists(SetupScript))
            throw new InvalidOperationException("Server-enforced SFTP permissions require a Linux CE package with the SFTP permission helper installed.");
        await RunAsync([SetupScript, "check"], token);
    }

    public Task ApplyAsync(SftpManagedUser user, CancellationToken token)
        => user.Permissions == SftpFilePermissions.All
            ? RemoveAsync(user.UserName, token)
            : RunAsync([SetupScript, "apply", user.UserName, user.Folder.ChrootPath, ((int)user.Permissions).ToString(System.Globalization.CultureInfo.InvariantCulture)], token);

    public Task RemoveAsync(string userName, CancellationToken token)
        => File.Exists(SetupScript) ? RunAsync([SetupScript, "remove", userName], token) : Task.CompletedTask;

    private async Task RunAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync(new LinuxCommandRequest("bash", args, true, TimeSpan.FromSeconds(60), "Apply server-enforced SFTP file permissions"), false, token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"SFTP permissions were not applied: {SftpSystemCommandHelper.BuildFailureDetail(result)}");
    }
}
