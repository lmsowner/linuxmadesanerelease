// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using LinuxMadeSane.Application.Contracts.Updates;
namespace LinuxMadeSane.Web.Services;

// Only a confirmed, authenticated launch can run this fixed command once in a new local console.
public sealed class ServerUpgradeLaunches(TimeProvider clock)
{
    // A pending counter must not gate access to Ubuntu's upgrader. Prepare packages in the
    // same interactive console, and stop here if preparation fails or the user cancels it.
    // Keep preparation and its postconditions in one shell; APT can exit zero with packages left behind.
    internal const string PreparationScript = """
        set -eu
        export LC_ALL=C
        echo "Preparing this server for its Ubuntu release upgrade, including phased updates."
        apt-get -o APT::Update::Error-Mode=any update
        apt-get -o APT::Get::Always-Include-Phased-Updates=true dist-upgrade
        echo "Checking that package preparation actually completed."
        audit=$(dpkg --audit)
        if [ -n "$audit" ]; then
            printf '%s\n' "$audit"
            echo "Package configuration is incomplete. Resolve the dpkg diagnostics above, then retry Upgrade server."
            exit 1
        fi
        pending=$(apt -o APT::Get::Always-Include-Phased-Updates=true list --upgradable 2>/dev/null)
        case "$pending" in
            */*)
                printf '%s\n' "$pending"
                echo "These packages still need updates. Administrator holds:"
                apt-mark showhold
                echo "Review holds, APT pins and the dependency diagnostics above. LMS will not remove holds automatically. Retry Upgrade server after resolving these blockers."
                exit 1
                ;;
        esac
        if [ -f /var/run/reboot-required ]; then
            echo "Package updates are complete. Reboot this server, then select Upgrade server again to continue."
            exit 1
        fi
        echo "No package updates remain. Starting the Ubuntu release upgrade."
        do-release-upgrade
        """;
    public static readonly string Command = "sudo sh -c '" + PreparationScript.Replace("'", "'\"'\"'") + "'\r";
    private sealed record Launch(Guid Owner, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Launch> pending = new(StringComparer.Ordinal);
    public string Issue(Guid owner)
    {
        foreach (var entry in pending.Where(entry => entry.Value.Expires <= clock.GetUtcNow())) pending.TryRemove(entry);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        pending[key] = new(owner, clock.GetUtcNow().AddMinutes(2));
        return key;
    }
    public bool Consume(string? key, Guid owner) => !string.IsNullOrEmpty(key) && pending.TryGetValue(key, out var launch)
        && launch.Owner == owner && launch.Expires > clock.GetUtcNow() && pending.TryRemove(new KeyValuePair<string, Launch>(key, launch));

    public static string? Blocker(HostSystemUpdateSnapshot status)
    {
        if (status.Job.State is HostSystemUpdateJobState.Refreshing or HostSystemUpdateJobState.Applying || status.ReleaseUpgrade.IsChecking)
            return "Another update operation is still running. Wait for it to finish, then retry.";
        if (!status.OperatingSystem.Id.Equals("ubuntu", StringComparison.OrdinalIgnoreCase)) return "This upgrade console is for Ubuntu servers.";
        if (!status.ReleaseUpgrade.ToolAvailable || !status.ReleaseUpgrade.IsAvailable) return status.ReleaseUpgrade.Summary;
        return null;
    }
}
