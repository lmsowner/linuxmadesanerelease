// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalUfwFirewallService : IFirewallManagementService, IHostedService, IDisposable
{
    public static readonly TimeSpan DefaultTrialDuration = TimeSpan.FromSeconds(30);

    private const string UfwExecutable = "ufw";
    private const string StatusInactive = "inactive";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PackageCommandTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly FirewallFileDefinition[] FirewallFiles =
    [
        new("/etc/ufw/user.rules", "0640"),
        new("/etc/ufw/user6.rules", "0640"),
        new("/etc/ufw/ufw.conf", "0644"),
        new("/etc/default/ufw", "0644")
    ];

    private readonly ILinuxCommandRunner commandRunner;
    private readonly FirewallTrialStorageSettings storageSettings;
    private readonly ILogger<LocalUfwFirewallService> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan trialDuration;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private FirewallRecoveryState? activeRecovery;
    private CancellationTokenSource? trialCancellation;
    private Task? trialMonitor;
    private bool disposed;

    public LocalUfwFirewallService(
        ILinuxCommandRunner commandRunner,
        FirewallTrialStorageSettings storageSettings,
        ILogger<LocalUfwFirewallService> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? trialDuration = null)
    {
        this.commandRunner = commandRunner;
        this.storageSettings = storageSettings;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.trialDuration = trialDuration ?? DefaultTrialDuration;
    }

    private string RecoveryPath => Path.Combine(storageSettings.DirectoryPath, "pending-firewall-trial.json");

    private string DisabledRulesPath => Path.Combine(storageSettings.DirectoryPath, "disabled-firewall-rules.json");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var recovery = await ReadPersistedRecoveryAsync(cancellationToken);
            if (recovery is null)
            {
                return;
            }

            logger.LogWarning(
                "Recovering unfinished UFW Auto Rollback change {TrialId} before accepting firewall changes",
                recovery.Trial.Id);
            await RestoreAsync(recovery, cancellationToken);
            DeletePersistedRecoveryOrThrow();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        trialCancellation?.Cancel();
        if (trialMonitor is not null)
        {
            try
            {
                await trialMonitor;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await operationGate.WaitAsync(CancellationToken.None);
        try
        {
            if (activeRecovery is null)
            {
                return;
            }

            await RestoreAsync(activeRecovery, CancellationToken.None);
            DeletePersistedRecoveryOrThrow();
            activeRecovery = null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not restore the active UFW Auto Rollback change while LMS was stopping");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<FirewallStatusViewModel> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadStatusAsync(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            if (activeRecovery is not null)
            {
                throw new InvalidOperationException("Keep or revert the current Auto Rollback firewall change before installing packages.");
            }

            var version = await commandRunner.RunAsync(
                new LinuxCommandRequest(UfwExecutable, ["--version"], false, CommandTimeout, "Check whether UFW is installed")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);
            if (version.ExitCode == 0)
            {
                return;
            }

            await RunRequiredPackageCommandAsync(
                "apt-get",
                ["update"],
                "Refresh package metadata before installing UFW",
                cancellationToken);
            await RunRequiredPackageCommandAsync(
                "apt-get",
                ["install", "--yes", "ufw"],
                "Install the optional UFW package",
                cancellationToken);

            var verification = await commandRunner.RunAsync(
                new LinuxCommandRequest(UfwExecutable, ["--version"], false, CommandTimeout, "Verify the UFW installation")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);
            if (verification.ExitCode != 0)
            {
                throw new InvalidOperationException($"UFW installation completed, but the ufw command is unavailable: {BuildFailureDetail(verification)}");
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<FirewallTrialViewModel> StartEnableTrialAsync(CancellationToken cancellationToken = default) =>
        StartTrialAsync(
            "Firewall enablement",
            "Existing configured rules are preserved. Current listeners are imported only when no firewall policy exists yet.",
            async (status, token) =>
            {
                if (status.IsActive)
                {
                    throw new InvalidOperationException("UFW is already active.");
                }

                var shouldImportListeners = status.Rules.Count == 0;
                if (shouldImportListeners && status.ListeningPorts.Count == 0)
                {
                    throw new InvalidOperationException(
                        "LMS could not find any non-loopback listening ports, so it refused to enable UFW without a safe access rule inventory.");
                }

                await RunRequiredUfwAsync(["default", "deny", "incoming"], "Set UFW incoming default to deny", token);
                await RunRequiredUfwAsync(["default", "allow", "outgoing"], "Set UFW outgoing default to allow", token);

                if (shouldImportListeners)
                {
                    foreach (var listener in status.ListeningPorts)
                    {
                        var arguments = BuildAllowArguments(
                            listener.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            listener.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                                ? FirewallProtocol.Tcp
                                : FirewallProtocol.Udp,
                            "any",
                            listener.Destination,
                            $"LMS imported {listener.Protocol}/{listener.Port}");
                        await RunRequiredUfwAsync(arguments, $"Import listening {listener.Protocol}/{listener.Port} into UFW", token);
                    }
                }

                await RunRequiredUfwAsync(["--force", "enable"], "Enable UFW", token);
            },
            cancellationToken);

    public Task<FirewallTrialViewModel> StartDisableTrialAsync(CancellationToken cancellationToken = default) =>
        StartTrialAsync(
            "Firewall disablement",
            "UFW is inactive and will be re-enabled automatically unless you keep this change.",
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("UFW is already inactive.");
                }

                await RunRequiredUfwAsync(["--force", "disable"], "Disable UFW", token);
            },
            cancellationToken);

    public Task<FirewallTrialViewModel> StartAllowRuleTrialAsync(
        FirewallAllowRuleEditor editor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var normalized = NormalizeEditor(editor);
        var detail = $"Allow {FormatProtocol(normalized.Protocol)}/{normalized.Port} from {normalized.Source} to {normalized.Destination}.";

        return StartTrialAsync(
            "New allow rule",
            detail,
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("Enable UFW before adding an active allow rule.");
                }

                var protocols = normalized.Protocol == FirewallProtocol.Any
                    ? new[] { FirewallProtocol.Tcp, FirewallProtocol.Udp }
                    : new[] { normalized.Protocol };
                foreach (var protocol in protocols)
                {
                    await RunRequiredUfwAsync(
                        BuildAllowArguments(
                            normalized.Port,
                            protocol,
                            normalized.Source,
                            normalized.Destination,
                            normalized.Comment),
                        $"Add UFW {FormatProtocol(protocol)} allow rule",
                        token);
                }
            },
            cancellationToken);
    }

    public Task<FirewallTrialViewModel> StartRemoveRuleTrialAsync(
        int ruleNumber,
        CancellationToken cancellationToken = default)
    {
        if (ruleNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleNumber));
        }

        return StartTrialAsync(
            "Rule removal",
            $"UFW rule {ruleNumber} has been removed.",
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("UFW must be active before a numbered rule can be removed.");
                }

                if (!status.Rules.Any(rule => rule.Number == ruleNumber))
                {
                    throw new InvalidOperationException($"UFW rule {ruleNumber} no longer exists. Refresh the firewall list and try again.");
                }

                await RunRequiredUfwAsync(
                    ["--force", "delete", ruleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    $"Remove UFW rule {ruleNumber}",
                    token);
            },
            cancellationToken);
    }

    public Task<FirewallTrialViewModel> StartEditAllowRuleTrialAsync(
        int ruleNumber,
        FirewallAllowRuleEditor editor,
        CancellationToken cancellationToken = default)
    {
        if (ruleNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleNumber));
        }

        ArgumentNullException.ThrowIfNull(editor);
        var normalized = NormalizeEditor(editor);
        var detail = $"UFW rule {ruleNumber} is replaced with allow {FormatProtocol(normalized.Protocol)}/{normalized.Port} from {normalized.Source} to {normalized.Destination}.";

        return StartTrialAsync(
            "Edited allow rule",
            detail,
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("UFW must be active before a numbered rule can be edited.");
                }

                var existingRule = status.Rules.SingleOrDefault(rule => rule.Number == ruleNumber);
                if (existingRule is null)
                {
                    throw new InvalidOperationException($"UFW rule {ruleNumber} no longer exists. Refresh the firewall list and try again.");
                }

                if (existingRule.EditableAllowRule is null)
                {
                    throw new InvalidOperationException($"UFW rule {ruleNumber} uses syntax that the simple allow-rule editor cannot safely replace.");
                }

                EnsureMatchingAddressFamily(normalized, existingRule.IsIpv6, ruleNumber);

                var protocols = normalized.Protocol == FirewallProtocol.Any
                    ? new[] { FirewallProtocol.Tcp, FirewallProtocol.Udp }
                    : new[] { normalized.Protocol };
                var insertionNumber = ruleNumber;
                foreach (var protocol in protocols)
                {
                    await RunRequiredUfwAsync(
                        BuildAllowArguments(
                            normalized.Port,
                            protocol,
                            normalized.Source,
                            normalized.Destination,
                            normalized.Comment,
                            insertionNumber),
                        $"Insert edited UFW {FormatProtocol(protocol)} allow rule at {insertionNumber}",
                        token);
                    insertionNumber++;
                }

                var displacedRuleNumber = ruleNumber + protocols.Length;
                await RunRequiredUfwAsync(
                    ["--force", "delete", displacedRuleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    $"Remove displaced old UFW rule {displacedRuleNumber} after editing",
                    token);
            },
            cancellationToken);
    }

    public Task<FirewallTrialViewModel> StartRemoveDisabledRuleTrialAsync(
        Guid disabledRuleId,
        CancellationToken cancellationToken = default)
    {
        if (disabledRuleId == Guid.Empty)
        {
            throw new ArgumentException("Select a disabled firewall rule.", nameof(disabledRuleId));
        }

        return StartTrialAsync(
            "Disabled rule removal",
            "The disabled UFW allow rule has been removed.",
            async (_, token) =>
            {
                var disabledRules = (await ReadDisabledRulesAsync(token)).ToList();
                var disabledRule = disabledRules.SingleOrDefault(rule => rule.Id == disabledRuleId);
                if (disabledRule is null)
                {
                    throw new InvalidOperationException("That disabled UFW rule no longer exists. Refresh the firewall list and try again.");
                }

                disabledRules.Remove(disabledRule);
                await PersistDisabledRulesAsync(disabledRules, token);
            },
            cancellationToken);
    }

    public Task<FirewallTrialViewModel> StartEditDisabledAllowRuleTrialAsync(
        Guid disabledRuleId,
        FirewallAllowRuleEditor editor,
        CancellationToken cancellationToken = default)
    {
        if (disabledRuleId == Guid.Empty)
        {
            throw new ArgumentException("Select a disabled firewall rule.", nameof(disabledRuleId));
        }

        ArgumentNullException.ThrowIfNull(editor);
        var normalized = NormalizeEditor(editor);
        var detail = $"The disabled rule now allows {FormatProtocol(normalized.Protocol)}/{normalized.Port} from {normalized.Source} to {normalized.Destination}.";

        return StartTrialAsync(
            "Edited disabled allow rule",
            detail,
            async (_, token) =>
            {
                var disabledRules = (await ReadDisabledRulesAsync(token)).ToList();
                var index = disabledRules.FindIndex(rule => rule.Id == disabledRuleId);
                if (index < 0)
                {
                    throw new InvalidOperationException("That disabled UFW rule no longer exists. Refresh the firewall list and try again.");
                }

                var disabledRule = disabledRules[index];
                EnsureMatchingAddressFamily(normalized, disabledRule.IsIpv6, disabledRule.FamilyPosition);
                disabledRules[index] = disabledRule with { Editor = normalized };
                await PersistDisabledRulesAsync(disabledRules, token);
            },
            cancellationToken);
    }

    public Task<FirewallTrialViewModel> StartRemoveAllRulesTrialAsync(
        CancellationToken cancellationToken = default) =>
        StartTrialAsync(
            "Removal of all firewall rules",
            "Every numbered UFW rule is removed, with incoming and routed traffic defaulting to deny.",
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("UFW must be active before all rules can be removed.");
                }

                var ruleNumbers = status.Rules
                    .Where(static rule => rule.Number.HasValue)
                    .Select(static rule => rule.Number!.Value)
                    .Distinct()
                    .OrderDescending()
                    .ToArray();
                if (ruleNumbers.Length == 0)
                {
                    throw new InvalidOperationException("There are no numbered UFW rules to remove.");
                }

                await RunRequiredUfwAsync(["default", "deny", "incoming"], "Set UFW incoming default to deny", token);
                await RunRequiredUfwAsync(["default", "deny", "routed"], "Set UFW routed default to deny", token);
                foreach (var number in ruleNumbers)
                {
                    await RunRequiredUfwAsync(
                        ["--force", "delete", number.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                        $"Remove UFW rule {number} during full lockdown",
                        token);
                }
            },
            cancellationToken);

    public Task<FirewallTrialViewModel> StartDisableAllowRuleTrialAsync(
        int ruleNumber,
        CancellationToken cancellationToken = default)
    {
        if (ruleNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleNumber));
        }

        return StartTrialAsync(
            "Firewall rule disablement",
            $"UFW rule {ruleNumber} is disabled and remains available to switch back on.",
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("UFW must be active before a rule can be disabled.");
                }

                var existingRule = status.Rules.SingleOrDefault(rule => rule.IsEnabled && rule.Number == ruleNumber);
                if (existingRule?.EditableAllowRule is null)
                {
                    throw new InvalidOperationException(
                        $"UFW rule {ruleNumber} uses syntax that LMS cannot safely preserve for later re-enablement.");
                }

                var editor = NormalizeEditor(existingRule.EditableAllowRule);
                EnsureMatchingAddressFamily(editor, existingRule.IsIpv6, ruleNumber);
                var familyPosition = status.Rules.Count(rule =>
                    rule.IsEnabled &&
                    rule.Number.HasValue &&
                    rule.IsIpv6 == existingRule.IsIpv6 &&
                    rule.Number.Value <= ruleNumber);
                var disabledRules = (await ReadDisabledRulesAsync(token)).ToList();
                disabledRules.Add(new DisabledFirewallRule(
                    Guid.NewGuid(),
                    editor,
                    existingRule.IsIpv6,
                    familyPosition,
                    timeProvider.GetUtcNow()));

                await PersistDisabledRulesAsync(disabledRules, token);
                await RunRequiredUfwAsync(
                    ["--force", "delete", ruleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    $"Disable UFW rule {ruleNumber}",
                    token);
            },
            cancellationToken);
    }

    public Task<FirewallTrialViewModel> StartEnableAllowRuleTrialAsync(
        Guid disabledRuleId,
        CancellationToken cancellationToken = default)
    {
        if (disabledRuleId == Guid.Empty)
        {
            throw new ArgumentException("Select a disabled firewall rule.", nameof(disabledRuleId));
        }

        return StartTrialAsync(
            "Firewall rule enablement",
            "The selected UFW allow rule is active again.",
            async (status, token) =>
            {
                if (!status.IsActive)
                {
                    throw new InvalidOperationException("UFW must be active before a disabled rule can be enabled.");
                }

                var disabledRules = (await ReadDisabledRulesAsync(token)).ToList();
                var disabledRule = disabledRules.SingleOrDefault(rule => rule.Id == disabledRuleId);
                if (disabledRule is null)
                {
                    throw new InvalidOperationException("That disabled UFW rule no longer exists. Refresh the firewall list and try again.");
                }

                var insertionNumber = ResolveDisabledRuleInsertionNumber(status.Rules, disabledRule);
                await RunRequiredUfwAsync(
                    BuildAllowArguments(
                        disabledRule.Editor.Port,
                        disabledRule.Editor.Protocol,
                        disabledRule.Editor.Source,
                        disabledRule.Editor.Destination,
                        disabledRule.Editor.Comment,
                        insertionNumber),
                    insertionNumber.HasValue
                        ? $"Re-enable UFW allow rule at {insertionNumber.Value}"
                        : "Re-enable UFW allow rule",
                    token);

                disabledRules.Remove(disabledRule);
                await PersistDisabledRulesAsync(disabledRules, token);
            },
            cancellationToken);
    }

    public async Task ConfirmTrialAsync(Guid trialId, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var recovery = RequireActiveTrial(trialId);
            DeletePersistedRecoveryOrThrow();
            trialCancellation?.Cancel();
            activeRecovery = null;
            logger.LogInformation("Kept UFW Auto Rollback change {TrialId}: {Title}", recovery.Trial.Id, recovery.Trial.Title);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RevertTrialAsync(Guid trialId, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var recovery = RequireActiveTrial(trialId);
            trialCancellation?.Cancel();
            await RestoreAsync(recovery, cancellationToken);
            DeletePersistedRecoveryOrThrow();
            activeRecovery = null;
            logger.LogInformation("Reverted UFW Auto Rollback change {TrialId}: {Title}", recovery.Trial.Id, recovery.Trial.Title);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal static FirewallAllowRuleEditor NormalizeEditor(FirewallAllowRuleEditor editor)
    {
        if (!Enum.IsDefined(editor.Protocol))
        {
            throw new InvalidOperationException("Select a supported firewall protocol.");
        }

        var port = NormalizePort(editor.Port);
        var source = NormalizeAddressOrCidr(editor.Source, "source");
        var destination = NormalizeAddressOrCidr(editor.Destination, "destination");
        var comment = string.IsNullOrWhiteSpace(editor.Comment)
            ? $"LMS allow {FormatProtocol(editor.Protocol)}/{port}"
            : editor.Comment.Trim();

        if (comment.Length > 80 || comment.Any(char.IsControl))
        {
            throw new InvalidOperationException("The firewall rule comment must be at most 80 printable characters.");
        }

        return new FirewallAllowRuleEditor
        {
            Port = port,
            Protocol = editor.Protocol,
            Source = source,
            Destination = destination,
            Comment = comment
        };
    }

    internal static IReadOnlyList<FirewallRuleViewModel> ParseNumberedRules(string output)
    {
        var rules = new List<FirewallRuleViewModel>();
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("[", StringComparison.Ordinal))
            {
                continue;
            }

            var numberMatch = Regex.Match(line, @"^\[\s*(?<number>\d+)\]\s*(?<body>.*)$", RegexOptions.CultureInvariant);
            if (!numberMatch.Success ||
                !int.TryParse(numberMatch.Groups["number"].Value, out var number))
            {
                continue;
            }

            var body = numberMatch.Groups["body"].Value;
            var ruleMatch = Regex.Match(
                body,
                @"^(?<to>.+?)\s{2,}(?<action>ALLOW|DENY|REJECT|LIMIT)(?:\s+(?<direction>IN|OUT|FWD))?\s{2,}(?<from>.*)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            if (!ruleMatch.Success)
            {
                rules.Add(new FirewallRuleViewModel(number, body, "Configured", string.Empty, body.Contains("(v6)", StringComparison.OrdinalIgnoreCase), string.Empty, line));
                continue;
            }

            var destination = ruleMatch.Groups["to"].Value.Trim();
            var action = ruleMatch.Groups["action"].Value.ToUpperInvariant();
            var direction = ruleMatch.Groups["direction"].Value.ToUpperInvariant();
            var sourceAndComment = ruleMatch.Groups["from"].Value.Trim();
            var commentIndex = sourceAndComment.IndexOf(" # ", StringComparison.Ordinal);
            var source = commentIndex >= 0 ? sourceAndComment[..commentIndex].Trim() : sourceAndComment;
            var comment = commentIndex >= 0 ? sourceAndComment[(commentIndex + 3)..].Trim() : string.Empty;
            var isIpv6 = destination.Contains("(v6)", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("(v6)", StringComparison.OrdinalIgnoreCase);

            var normalizedDestination = destination.Replace(" (v6)", string.Empty, StringComparison.OrdinalIgnoreCase);
            var normalizedSource = source.Replace(" (v6)", string.Empty, StringComparison.OrdinalIgnoreCase);
            rules.Add(new FirewallRuleViewModel(
                number,
                normalizedDestination,
                string.IsNullOrWhiteSpace(direction) ? action : $"{action} {direction}",
                normalizedSource,
                isIpv6,
                comment,
                line)
            {
                EditableAllowRule = TryBuildEditableAllowRule(
                    normalizedDestination,
                    string.IsNullOrWhiteSpace(direction) ? action : $"{action} {direction}",
                    normalizedSource,
                    isIpv6,
                    comment)
            });
        }

        return rules;
    }

    internal static IReadOnlyList<FirewallRuleViewModel> ParseAddedRules(string output) =>
        SplitLines(output)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("ufw ", StringComparison.OrdinalIgnoreCase))
            .Select(static line => new FirewallRuleViewModel(
                null,
                line[4..],
                "Configured",
                "Loads when enabled",
                line.Contains("(v6)", StringComparison.OrdinalIgnoreCase),
                string.Empty,
                line))
            .ToArray();

    internal static IReadOnlyList<FirewallListeningPortViewModel> ParseListeningPorts(
        string output,
        IReadOnlySet<string>? localAddresses = null)
    {
        localAddresses ??= LoadLocalUnicastAddresses();
        var candidates = new List<FirewallListeningPortViewModel>();

        foreach (var rawLine in SplitLines(output))
        {
            var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 ||
                (parts[0] is not "tcp" and not "udp") ||
                !TryParseSocketEndpoint(parts[4], out var address, out var port))
            {
                continue;
            }

            if (!address.Equals("any", StringComparison.Ordinal) &&
                (!IPAddress.TryParse(address, out var parsedAddress) ||
                 IPAddress.IsLoopback(parsedAddress) ||
                 !localAddresses.Contains(parsedAddress.ToString())))
            {
                continue;
            }

            candidates.Add(new FirewallListeningPortViewModel(parts[0], port, address));
        }

        var wildcardKeys = candidates
            .Where(static endpoint => endpoint.Destination == "any")
            .Select(static endpoint => $"{endpoint.Protocol}/{endpoint.Port}")
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(endpoint => endpoint.Destination == "any" ||
                               !wildcardKeys.Contains($"{endpoint.Protocol}/{endpoint.Port}"))
            .Distinct()
            .OrderBy(static endpoint => endpoint.Port)
            .ThenBy(static endpoint => endpoint.Protocol, StringComparer.Ordinal)
            .ThenBy(static endpoint => endpoint.Destination, StringComparer.Ordinal)
            .ToArray();
    }

    internal static FirewallAllowRuleEditor? TryBuildEditableAllowRule(
        string destination,
        string action,
        string source,
        bool isIpv6,
        string comment)
    {
        if (!action.Equals("ALLOW", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("ALLOW IN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var endpointMatch = Regex.Match(
            destination,
            @"^(?:(?<address>\S+)\s+)?(?<port>\d{1,5}(?::\d{1,5})?)(?:/(?<protocol>tcp|udp))?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!endpointMatch.Success)
        {
            return null;
        }

        var protocol = endpointMatch.Groups["protocol"].Value.ToLowerInvariant() switch
        {
            "tcp" => FirewallProtocol.Tcp,
            "udp" => FirewallProtocol.Udp,
            _ => FirewallProtocol.Any
        };
        var anyAddress = isIpv6 ? "::/0" : "0.0.0.0/0";
        var editor = new FirewallAllowRuleEditor
        {
            Port = endpointMatch.Groups["port"].Value,
            Protocol = protocol,
            Source = source.Equals("Anywhere", StringComparison.OrdinalIgnoreCase) ||
                     source.Equals("any", StringComparison.OrdinalIgnoreCase)
                ? anyAddress
                : source,
            Destination = endpointMatch.Groups["address"].Success
                ? endpointMatch.Groups["address"].Value
                : anyAddress,
            Comment = comment
        };

        try
        {
            return NormalizeEditor(editor);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static void EnsureMatchingAddressFamily(
        FirewallAllowRuleEditor editor,
        bool expectedIpv6,
        int ruleNumber)
    {
        var concreteAddresses = new[] { editor.Source, editor.Destination }
            .Where(static address => !address.Equals("any", StringComparison.OrdinalIgnoreCase))
            .Select(static address => address.Split('/', 2)[0])
            .Select(IPAddress.Parse)
            .ToArray();
        if (concreteAddresses.Length == 0)
        {
            var expectedRange = expectedIpv6 ? "::/0" : "0.0.0.0/0";
            throw new InvalidOperationException(
                $"Keep at least one address on UFW rule {ruleNumber} in its existing IP family. Use {expectedRange} instead of any, or add a separate rule.");
        }

        var expectedFamily = expectedIpv6
            ? System.Net.Sockets.AddressFamily.InterNetworkV6
            : System.Net.Sockets.AddressFamily.InterNetwork;
        if (concreteAddresses.Any(address => address.AddressFamily != expectedFamily))
        {
            throw new InvalidOperationException(
                $"UFW rule {ruleNumber} is an IPv{(expectedIpv6 ? "6" : "4")} rule. Add a separate rule instead of changing its IP family during an edit.");
        }
    }

    private static int? ResolveDisabledRuleInsertionNumber(
        IReadOnlyList<FirewallRuleViewModel> rules,
        DisabledFirewallRule disabledRule)
    {
        var liveRules = rules
            .Where(static rule => rule.IsEnabled && rule.Number.HasValue)
            .ToArray();
        var familyRuleCount = liveRules.Count(rule => rule.IsIpv6 == disabledRule.IsIpv6);
        if (familyRuleCount == 0 || disabledRule.FamilyPosition > familyRuleCount)
        {
            return null;
        }

        if (!disabledRule.IsIpv6)
        {
            return disabledRule.FamilyPosition;
        }

        var ipv4RuleCount = liveRules.Count(static rule => !rule.IsIpv6);
        return ipv4RuleCount + disabledRule.FamilyPosition;
    }

    private static FirewallRuleViewModel BuildDisabledRuleViewModel(DisabledFirewallRule disabledRule)
    {
        var editor = CloneEditor(disabledRule.Editor);
        var anyAddress = disabledRule.IsIpv6 ? "::/0" : "0.0.0.0/0";
        var portAndProtocol = editor.Protocol == FirewallProtocol.Any
            ? editor.Port
            : $"{editor.Port}/{FormatProtocol(editor.Protocol)}";
        var destination = IsAnyAddress(editor.Destination, anyAddress)
            ? portAndProtocol
            : $"{editor.Destination} {portAndProtocol}";
        var source = IsAnyAddress(editor.Source, anyAddress)
            ? "Anywhere"
            : editor.Source;

        return new FirewallRuleViewModel(
            null,
            destination,
            "ALLOW IN",
            source,
            disabledRule.IsIpv6,
            editor.Comment,
            "Disabled by Linux Made Sane")
        {
            EditableAllowRule = editor,
            DisabledRuleId = disabledRule.Id,
            IsEnabled = false
        };
    }

    private static bool IsAnyAddress(string value, string familyAnyAddress) =>
        value.Equals("any", StringComparison.OrdinalIgnoreCase) ||
        value.Equals(familyAnyAddress, StringComparison.OrdinalIgnoreCase);

    private static FirewallAllowRuleEditor CloneEditor(FirewallAllowRuleEditor editor) =>
        new()
        {
            Port = editor.Port,
            Protocol = editor.Protocol,
            Source = editor.Source,
            Destination = editor.Destination,
            Comment = editor.Comment
        };

    private async Task<FirewallTrialViewModel> StartTrialAsync(
        string title,
        string detail,
        Func<FirewallStatusViewModel, CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            if (activeRecovery is not null)
            {
                throw new InvalidOperationException("Keep or revert the current Auto Rollback firewall change before starting another one.");
            }

            var status = await ReadStatusAsync(cancellationToken);
            EnsureManageable(status);
            var trial = new FirewallTrialViewModel(
                Guid.NewGuid(),
                title,
                detail,
                timeProvider.GetUtcNow().Add(trialDuration));
            var recovery = new FirewallRecoveryState(
                trial,
                status.IsActive,
                await CaptureFirewallFilesAsync(cancellationToken))
            {
                DisabledRulesFile = await CaptureDisabledRulesFileAsync(cancellationToken)
            };

            await PersistRecoveryAsync(recovery, cancellationToken);
            activeRecovery = recovery;

            try
            {
                await mutation(status, cancellationToken);
            }
            catch
            {
                try
                {
                    await RestoreAsync(recovery, CancellationToken.None);
                    DeletePersistedRecoveryOrThrow();
                    activeRecovery = null;
                }
                catch (Exception restoreException)
                {
                    logger.LogError(restoreException, "Immediate UFW rollback failed for change {TrialId}", trial.Id);
                }

                throw;
            }

            ArmAutomaticRevert(recovery);
            return trial;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<FirewallStatusViewModel> ReadStatusAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Unavailable("UFW firewall management is available only on Linux hosts.");
        }

        var version = await commandRunner.RunAsync(
            new LinuxCommandRequest(UfwExecutable, ["--version"], false, CommandTimeout, "Check whether UFW is installed")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);
        if (version.ExitCode != 0)
        {
            return Unavailable("UFW is not installed on this host. Install the ufw package before using this page.");
        }

        var verbose = await RunUfwAsync(["status", "verbose"], "Read UFW status", cancellationToken);
        if (verbose.ExitCode != 0)
        {
            return new FirewallStatusViewModel(
                true,
                false,
                false,
                "unknown",
                "unknown",
                "unknown",
                "unknown",
                [],
                [],
                activeRecovery?.Trial,
                $"LMS cannot read UFW with non-interactive sudo: {BuildFailureDetail(verbose)}");
        }

        var isActive = verbose.StandardOutput.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        var ruleResult = await RunUfwAsync(
            isActive ? ["status", "numbered"] : ["show", "added"],
            isActive ? "Read numbered UFW rules" : "Read configured UFW rules",
            cancellationToken);
        var listenerResult = await commandRunner.RunAsync(
            new LinuxCommandRequest("ss", ["-H", "-lntu"], false, CommandTimeout, "Inventory non-loopback listening ports")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var liveRules = ruleResult.ExitCode == 0
            ? isActive
                ? ParseNumberedRules(ruleResult.StandardOutput)
                : ParseAddedRules(ruleResult.StandardOutput)
            : [];
        IReadOnlyList<DisabledFirewallRule> disabledRules = [];
        string? disabledRulesWarning = null;
        try
        {
            disabledRules = await ReadDisabledRulesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            disabledRulesWarning = $"LMS could not read its disabled-rule inventory: {exception.Message}";
        }

        var rules = liveRules
            .Concat(disabledRules.Select(BuildDisabledRuleViewModel))
            .ToArray();
        var listeners = listenerResult.ExitCode == 0
            ? ParseListeningPorts(listenerResult.StandardOutput)
            : [];
        var policies = ParsePolicies(verbose.StandardOutput);
        var warnings = new List<string>();
        if (ruleResult.ExitCode != 0)
        {
            warnings.Add($"UFW status was read, but its rules could not be listed: {BuildFailureDetail(ruleResult)}");
        }

        if (listenerResult.ExitCode != 0)
        {
            warnings.Add("UFW status is available, but LMS cannot inventory current listening ports. Firewall enablement is blocked to avoid locking you out.");
        }

        if (disabledRulesWarning is not null)
        {
            warnings.Add(disabledRulesWarning);
        }

        var warning = warnings.Count == 0 ? null : string.Join(' ', warnings);

        return new FirewallStatusViewModel(
            true,
            true,
            isActive,
            policies.Incoming,
            policies.Outgoing,
            policies.Routed,
            policies.Logging,
            rules,
            listeners,
            activeRecovery?.Trial,
            warning);
    }

    private void ArmAutomaticRevert(FirewallRecoveryState recovery)
    {
        trialCancellation?.Cancel();
        trialCancellation?.Dispose();
        trialCancellation = new CancellationTokenSource();
        trialMonitor = RevertAfterDelayAsync(recovery.Trial.Id, trialCancellation.Token);
    }

    private async Task RevertAfterDelayAsync(Guid trialId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(trialDuration, timeProvider, cancellationToken);
            await operationGate.WaitAsync(CancellationToken.None);
            try
            {
                if (activeRecovery?.Trial.Id != trialId)
                {
                    return;
                }

                await RestoreAsync(activeRecovery, CancellationToken.None);
                DeletePersistedRecoveryOrThrow();
                activeRecovery = null;
                logger.LogInformation("Automatically reverted expired UFW change {TrialId}", trialId);
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic UFW rollback failed for change {TrialId}", trialId);
        }
    }

    private async Task<IReadOnlyList<FirewallFileSnapshot>> CaptureFirewallFilesAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<FirewallFileSnapshot>(FirewallFiles.Length);
        foreach (var definition in FirewallFiles)
        {
            var read = await commandRunner.RunAsync(
                new LinuxCommandRequest("cat", [definition.Path], true, CommandTimeout, $"Snapshot {definition.Path} before UFW change"),
                dryRun: false,
                cancellationToken);
            if (read.ExitCode == 0)
            {
                snapshots.Add(new FirewallFileSnapshot(definition.Path, definition.Mode, true, read.StandardOutput));
                continue;
            }

            if (read.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                snapshots.Add(new FirewallFileSnapshot(definition.Path, definition.Mode, false, string.Empty));
                continue;
            }

            throw new InvalidOperationException($"LMS could not snapshot {definition.Path} before changing UFW: {BuildFailureDetail(read)}");
        }

        return snapshots;
    }

    private async Task<LocalFileSnapshot> CaptureDisabledRulesFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DisabledRulesPath))
        {
            return new LocalFileSnapshot(false, string.Empty);
        }

        return new LocalFileSnapshot(
            true,
            await File.ReadAllTextAsync(DisabledRulesPath, cancellationToken));
    }

    private async Task RestoreAsync(FirewallRecoveryState recovery, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"linuxmadesane-firewall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            foreach (var file in recovery.Files)
            {
                if (!file.Existed)
                {
                    await RunRequiredAsync("rm", ["-f", file.Path], true, $"Remove trial-created UFW file {file.Path}", cancellationToken);
                    continue;
                }

                var tempPath = Path.Combine(tempDirectory, Path.GetFileName(file.Path));
                await File.WriteAllTextAsync(tempPath, file.Content, cancellationToken);
                await RunRequiredAsync(
                    "install",
                    ["-o", "root", "-g", "root", "-m", file.Mode, tempPath, file.Path],
                    true,
                    $"Restore UFW file {file.Path}",
                    cancellationToken);
            }

            await RunRequiredUfwAsync(
                recovery.WasActive ? ["--force", "enable"] : ["--force", "disable"],
                recovery.WasActive ? "Restore active UFW state" : "Restore inactive UFW state",
                cancellationToken);

            if (recovery.DisabledRulesFile is not null)
            {
                await RestoreDisabledRulesFileAsync(recovery.DisabledRulesFile, cancellationToken);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task RestoreDisabledRulesFileAsync(
        LocalFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Existed)
        {
            File.Delete(DisabledRulesPath);
            return;
        }

        await WritePrivateLocalFileAsync(DisabledRulesPath, snapshot.Content, cancellationToken);
    }

    private async Task<IReadOnlyList<DisabledFirewallRule>> ReadDisabledRulesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(DisabledRulesPath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(DisabledRulesPath, cancellationToken);
        var storedRules = JsonSerializer.Deserialize<DisabledFirewallRule[]>(json, SerializerOptions) ?? [];
        var rules = new List<DisabledFirewallRule>(storedRules.Length);
        var knownIds = new HashSet<Guid>();
        foreach (var storedRule in storedRules)
        {
            if (storedRule.Id == Guid.Empty || !knownIds.Add(storedRule.Id) || storedRule.FamilyPosition <= 0)
            {
                throw new InvalidOperationException("The disabled firewall rule inventory contains an invalid entry.");
            }

            var editor = NormalizeEditor(storedRule.Editor);
            EnsureMatchingAddressFamily(editor, storedRule.IsIpv6, storedRule.FamilyPosition);
            rules.Add(storedRule with { Editor = editor });
        }

        return rules;
    }

    private async Task PersistDisabledRulesAsync(
        IReadOnlyList<DisabledFirewallRule> rules,
        CancellationToken cancellationToken)
    {
        if (rules.Count == 0)
        {
            File.Delete(DisabledRulesPath);
            return;
        }

        await WritePrivateLocalFileAsync(
            DisabledRulesPath,
            JsonSerializer.Serialize(rules, SerializerOptions),
            cancellationToken);
    }

    private async Task WritePrivateLocalFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.DirectoryPath);
        TrySetPrivateDirectoryMode(storageSettings.DirectoryPath);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        TrySetPrivateFileMode(tempPath);
        File.Move(tempPath, path, overwrite: true);
    }

    private async Task PersistRecoveryAsync(FirewallRecoveryState recovery, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.DirectoryPath);
        TrySetPrivateDirectoryMode(storageSettings.DirectoryPath);
        var tempPath = $"{RecoveryPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(recovery, SerializerOptions), cancellationToken);
        TrySetPrivateFileMode(tempPath);
        File.Move(tempPath, RecoveryPath, overwrite: true);
    }

    private async Task<FirewallRecoveryState?> ReadPersistedRecoveryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RecoveryPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(RecoveryPath, cancellationToken);
        return JsonSerializer.Deserialize<FirewallRecoveryState>(json, SerializerOptions) ??
               throw new InvalidOperationException("The pending firewall recovery file is invalid.");
    }

    private void DeletePersistedRecoveryOrThrow()
    {
        File.Delete(RecoveryPath);
    }

    private async Task<LinuxCommandResult> RunUfwAsync(
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken) =>
        await commandRunner.RunAsync(
            new LinuxCommandRequest(UfwExecutable, arguments, true, CommandTimeout, description),
            dryRun: false,
            cancellationToken);

    private async Task RunRequiredUfwAsync(
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await RunUfwAsync(arguments, description, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{description} failed: {BuildFailureDetail(result)}");
        }
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool requiresSudo,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, CommandTimeout, description),
            dryRun: false,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{description} failed: {BuildFailureDetail(result)}");
        }
    }

    private async Task RunRequiredPackageCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, true, PackageCommandTimeout, description),
            dryRun: false,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{description} failed: {BuildFailureDetail(result)}");
        }
    }

    private static IReadOnlyList<string> BuildAllowArguments(
        string port,
        FirewallProtocol protocol,
        string source,
        string destination,
        string comment,
        int? insertionNumber = null)
    {
        var arguments = insertionNumber.HasValue
            ? new List<string>
            {
                "insert",
                insertionNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "allow"
            }
            : ["allow"];
        if (protocol != FirewallProtocol.Any)
        {
            arguments.Add("proto");
            arguments.Add(FormatProtocol(protocol));
        }

        arguments.Add("from");
        arguments.Add(source);
        arguments.Add("to");
        arguments.Add(destination);
        arguments.Add("port");
        arguments.Add(port);
        arguments.Add("comment");
        arguments.Add(comment);
        return arguments;
    }

    private static string NormalizePort(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        var parts = trimmed.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 ||
            parts.Any(part => !int.TryParse(part, out var port) || port is < 1 or > 65535))
        {
            throw new InvalidOperationException("Enter a port from 1 to 65535, or a range such as 8000:8010.");
        }

        if (parts.Length == 2 && int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) >=
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("The first port in a range must be lower than the last port.");
        }

        return string.Join(':', parts.Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string NormalizeAddressOrCidr(string? value, string fieldName)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return "any";
        }

        if (trimmed.Contains('%'))
        {
            throw new InvalidOperationException($"The {fieldName} address cannot contain an interface scope.");
        }

        var parts = trimmed.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            throw new InvalidOperationException($"Enter the {fieldName} as any, an IP address, or a CIDR network.");
        }

        if (parts.Length == 1)
        {
            return address.ToString();
        }

        var maximumPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > maximumPrefix)
        {
            throw new InvalidOperationException($"The {fieldName} CIDR prefix must be between 0 and {maximumPrefix}.");
        }

        return $"{address}/{prefix}";
    }

    private static bool TryParseSocketEndpoint(string endpoint, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out port) || port is < 1 or > 65535)
        {
            return false;
        }

        address = endpoint[..separator].Trim('[', ']');
        var scopeIndex = address.IndexOf('%');
        if (scopeIndex >= 0)
        {
            address = address[..scopeIndex];
        }

        if (address is "*" or "0.0.0.0" or "::")
        {
            address = "any";
        }

        return true;
    }

    private static IReadOnlySet<string> LoadLocalUnicastAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(static networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Select(static address => address.Address.ToString())
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (NetworkInformationException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static FirewallPolicies ParsePolicies(string output)
    {
        var incoming = "unknown";
        var outgoing = "unknown";
        var routed = "unknown";
        var logging = "unknown";

        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Logging:", StringComparison.OrdinalIgnoreCase))
            {
                logging = line["Logging:".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("Default:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(
                         line,
                         @"(?<policy>[a-z]+)\s+\((?<direction>incoming|outgoing|routed)\)",
                         RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                var policy = match.Groups["policy"].Value.ToLowerInvariant();
                switch (match.Groups["direction"].Value.ToLowerInvariant())
                {
                    case "incoming":
                        incoming = policy;
                        break;
                    case "outgoing":
                        outgoing = policy;
                        break;
                    case "routed":
                        routed = policy;
                        break;
                }
            }
        }

        return new FirewallPolicies(incoming, outgoing, routed, logging);
    }

    private FirewallRecoveryState RequireActiveTrial(Guid trialId)
    {
        if (activeRecovery?.Trial.Id != trialId)
        {
            throw new InvalidOperationException("That Auto Rollback firewall change is no longer active. Refresh the firewall status before making another change.");
        }

        return activeRecovery;
    }

    private static void EnsureManageable(FirewallStatusViewModel status)
    {
        if (!status.IsInstalled)
        {
            throw new InvalidOperationException("UFW is not installed on this host.");
        }

        if (!status.CanManage)
        {
            throw new InvalidOperationException(status.Warning ?? "LMS cannot manage UFW with non-interactive sudo.");
        }
    }

    private static FirewallStatusViewModel Unavailable(string warning) =>
        new(false, false, false, StatusInactive, "unknown", "unknown", "unknown", [], [], null, warning);

    private static string BuildFailureDetail(LinuxCommandResult result) =>
        FirstNonEmptyLine(result.StandardError) ??
        FirstNonEmptyLine(result.StandardOutput) ??
        $"exit code {result.ExitCode}";

    private static string? FirstNonEmptyLine(string? value) =>
        SplitLines(value ?? string.Empty).FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))?.Trim();

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string FormatProtocol(FirewallProtocol protocol) => protocol switch
    {
        FirewallProtocol.Any => "any",
        FirewallProtocol.Tcp => "tcp",
        FirewallProtocol.Udp => "udp",
        _ => throw new InvalidOperationException("Unsupported firewall protocol.")
    };

    private static void TrySetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void TrySetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        trialCancellation?.Cancel();
        trialCancellation?.Dispose();
    }

    private sealed record FirewallFileDefinition(string Path, string Mode);

    private sealed record FirewallFileSnapshot(string Path, string Mode, bool Existed, string Content);

    private sealed record FirewallRecoveryState(
        FirewallTrialViewModel Trial,
        bool WasActive,
        IReadOnlyList<FirewallFileSnapshot> Files)
    {
        public LocalFileSnapshot? DisabledRulesFile { get; init; }
    }

    private sealed record LocalFileSnapshot(bool Existed, string Content);

    private sealed record DisabledFirewallRule(
        Guid Id,
        FirewallAllowRuleEditor Editor,
        bool IsIpv6,
        int FamilyPosition,
        DateTimeOffset DisabledAtUtc);

    private sealed record FirewallPolicies(string Incoming, string Outgoing, string Routed, string Logging);
}
