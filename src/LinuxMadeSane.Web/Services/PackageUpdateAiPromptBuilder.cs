// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Updates;

namespace LinuxMadeSane.Web.Services;

public static class PackageUpdateAiPromptBuilder
{
    private const int MaxRecentLogCharacters = 4_000;

    public static string Build(
        string statusSummary,
        IReadOnlyList<string> recentLogLines,
        IReadOnlyList<HostUpgradeablePackage> pendingPackages,
        bool needsAttention)
    {
        var recentLog = TrimRecentLog(string.Join(Environment.NewLine, recentLogLines.TakeLast(80)));
        var pendingPackageList = pendingPackages.Count == 0
            ? "No packages were pending when this session opened."
            : string.Join(
                Environment.NewLine,
                pendingPackages.Select(package =>
                    $"- {package.Name}: {package.CurrentVersion} -> {package.CandidateVersion}"));

        var objective = pendingPackages.Count > 0
            ? needsAttention
                ? "Repair the package manager, then install every currently pending package update."
                : "Install every currently pending package update."
            : needsAttention
                ? "Repair the package update failure and restore a healthy package state."
                : "Verify that the package manager is healthy and that no package updates are pending.";

        var completionRequirement = pendingPackages.Count > 0
            ? "Do not stop after refreshing APT metadata or repairing APT. Continue until the package upgrade has run successfully. For a healthy package manager, run a non-interactive `apt-get upgrade -y` with `Dpkg::Options::=--force-confold`. Before reporting completion, verify each package listed below is installed at its current candidate version and is absent from `apt list --upgradable`. If APT reports that a package was kept back, resolve that package with a dependency-aware upgrade and verify it again."
            : "Verify that `apt list --upgradable` confirms the current package state before reporting completion.";

        return $"""
{objective}

Inspect the active APT/dpkg transaction state and the recent LMS update log first. Do not start a second package transaction while one is running. If APT or dpkg is broken, finish interrupted configuration and repair dependencies before applying updates. Preserve existing local configuration files and use non-interactive APT commands. Do not perform an Ubuntu distribution release upgrade or update Linux Made Sane itself.

After any package installation, run a final `apt-get update` and treat every actionable repository warning as unresolved. Package post-install scripts can recreate a duplicate repository definition. Keep one valid signed source, disable the duplicate, persist the package's repository preference so its next upgrade does not recreate that duplicate, then repeat `apt-get update`. Report success only after the final metadata refresh completes without actionable warnings.

{completionRequirement}

Pending packages captured by LMS:
{pendingPackageList}

LMS update status:
{statusSummary}

Recent Host update log:
```text
{recentLog}
```
""";
    }

    private static string TrimRecentLog(string recentLog)
    {
        var normalized = recentLog.Trim();
        if (normalized.Length <= MaxRecentLogCharacters)
        {
            return normalized;
        }

        return $"[earlier log lines omitted]{Environment.NewLine}{normalized[^MaxRecentLogCharacters..]}";
    }
}
