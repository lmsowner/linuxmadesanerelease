// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Globalization;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed class LmsUiRepairService(IManagedHostStore hosts, ISavedCredentialAccessContext access,
    ICommandExecutionService commands, IHostAdministratorCredentials administrators, LmsWebUiProbe probe,
    ILogger<LmsUiRepairService> logger)
{
    private static readonly string Script = LoadScript();

    public async Task<LmsWebUiStatus> RepairAsync(Guid hostId, Func<string, Task> progress, CancellationToken token)
    {
        if (await access.GetAuthenticatedUserIdAsync(token) is null)
            throw new InvalidOperationException("Sign in to the managing LMS before repairing a host.");
        var host = await hosts.GetAsync(hostId, token);
        if (host?.Kind != ManagedHostKind.LmsHost || ManagedHostCapabilities.Describe(host).IsLocalLmsHost ||
            AiLocalMachine.IsLoopbackHostname(host.Hostname))
            throw new InvalidOperationException("Select a registered remote LMS host.");
        host = host with { DefaultWorkingDirectory = "/" };
        var status = await probe.CheckAsync(host, token);
        if (status.Available) return status;

        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        if (!status.AccessDenied)
        {
            await progress("Checking the installed LMS service over SSH…");
            var state = await RunAsync(host, "start", stamp, token);
            if (state == "started") await progress("LMS started. Checking its web UI…");
            for (var attempt = 0; attempt < 6; attempt++)
            {
                status = await probe.CheckAsync(host, token);
                if (status.Available) return status;
                if (status.AccessDenied || state != "started") break;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            if (!status.AccessDenied)
                throw new InvalidOperationException("The LMS service is running, but its web page could not be opened from this LMS. Check the listener, route and firewall for the host’s web port.");
        }

        await progress("Checking the LMS interface policy over SSH and restoring MFA sign-in…");
        var verified = false;
        var mayHaveChanged = true;
        try
        {
            var outcome = await RunAsync(host, "restore", stamp, token);
            mayHaveChanged = outcome == "changed";
            if (outcome == "needs-enrollment")
                throw new InvalidOperationException("LMS has no enabled MFA account yet. Complete first-time setup using the installer’s setup code.");
            await progress("Opening the remote LMS page again to verify access…");
            status = await probe.CheckAsync(host, token);
            verified = status.Available;
            if (!verified)
                throw new InvalidOperationException(mayHaveChanged
                    ? "The page still did not open. The repair could not be verified."
                    : "The built-in public access rule does not explain this denial. Check the host’s custom interface rules and reverse proxy.");
            logger.LogInformation("Verified repaired LMS web access on registered host {HostId}", hostId);
            return status;
        }
        catch (RepairCommandRejectedException)
        {
            mayHaveChanged = false;
            throw;
        }
        finally
        {
            if (mayHaveChanged && !verified)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try { await RunAsync(host, "revert", stamp, cleanup.Token); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not confirm LMS access rollback on host {HostId}", hostId);
                    throw new InvalidOperationException("The repair was interrupted. Reconnect over SSH and check the public interface rule. Its rollback could not be confirmed.");
                }
            }
        }
    }

    private async Task<string> RunAsync(ManagedHost host, string operation, string stamp, CancellationToken token)
    {
        var command = "python3 -c " + LogSearchService.Quote(Script) + " " + operation + " " +
            LogSearchService.Quote(stamp) + " \"$SSH_CONNECTION\"";
        var root = host.Username == "root";
        var result = await Task.Run(() => commands.ExecuteAsync(host, root ? command : "sudo -n -- " + command,
            cancellationToken: token), token);
        if (!root && !result.IsSuccess && result.StandardError.Contains("sudo:", StringComparison.OrdinalIgnoreCase) &&
            commands is ICommandExecutionInputService withInput && await administrators.ResolveAsync(host.Id, host.Username, token) is { } password)
            result = await Task.Run(() => withInput.ExecuteAsync(host, "sudo -S -p '' -- " + command, password,
                cancellationToken: token), token);
        if (result.ExitCode < 0)
            throw new IOException("SSH disconnected before the repair command returned a result.");
        if (!result.IsSuccess)
        {
            var explanation = result.StandardError.Trim();
            if (explanation.Contains("sudo:", StringComparison.OrdinalIgnoreCase))
                throw new RepairCommandRejectedException("SSH connected, but repair needs administrator access. Check this host’s saved sudo credentials.");
            throw new RepairCommandRejectedException(explanation.StartsWith("Could not repair LMS:", StringComparison.Ordinal)
                ? explanation : "The SSH repair command failed. Check the installed LMS service and the SSH account’s administrator access.");
        }
        return result.StandardOutput.Trim();
    }

    private sealed class RepairCommandRejectedException(string message) : InvalidOperationException(message);

    private static string LoadScript()
    {
        using var stream = typeof(LmsUiRepairService).Assembly.GetManifestResourceStream("Lms.Repair.Access")
            ?? throw new InvalidOperationException("The LMS web access repair resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
