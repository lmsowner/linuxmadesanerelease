// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSocatPortForwardingService(
    ILinuxCommandRunner commandRunner,
    PortForwardStorageSettings storageSettings) : IPortForwardingService
{
    private const string SocatExecutable = "/usr/bin/socat";
    private const string UnitDirectory = "/etc/systemd/system";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex PidPattern = new(@"pid=(?<pid>\d+)", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim operationGate = new(1, 1);

    private string RulesPath => Path.Combine(storageSettings.DirectoryPath, "port-forwards.json");

    public async Task<PortForwardingOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var version = await RunAsync(
                SocatExecutable,
                ["-V"],
                false,
                "Check whether socat is installed",
                cancellationToken,
                optional: true);
            var installed = version.ExitCode == 0;
            var rules = await ReadRulesAsync(cancellationToken);
            var interfaces = LoadInterfaceOptions();
            var views = new List<PortForwardRuleViewModel>(rules.Count);
            foreach (var rule in rules.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var state = await ReadUnitStateAsync(rule.Id, cancellationToken);
                views.Add(new PortForwardRuleViewModel(
                    rule.Id,
                    rule.Name,
                    rule.ListenAddress,
                    ResolveInterfaceLabel(rule.ListenAddress, interfaces),
                    rule.SourcePort,
                    rule.Protocol,
                    rule.DestinationAddress,
                    rule.DestinationPort,
                    rule.IsEnabled,
                    state.Equals("active", StringComparison.OrdinalIgnoreCase),
                    rule.IsEnabled ? state : "disabled",
                    BuildUnitName(rule.Id)));
            }

            return new PortForwardingOverview(
                installed,
                true,
                installed ? FirstNonEmptyLine(version.StandardOutput, version.StandardError) ?? "Installed" : "Not installed",
                interfaces,
                views,
                installed ? null : "socat is not installed. Install it here before creating a port forward.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task InstallSocatAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var current = await RunAsync(SocatExecutable, ["-V"], false, "Check whether socat is installed", cancellationToken, optional: true);
            if (current.ExitCode == 0)
            {
                return;
            }

            EnsureSuccess(
                await RunAsync("apt-get", ["update"], true, "Refresh package metadata before installing socat", cancellationToken, timeout: PackageTimeout),
                "Package metadata could not be refreshed");
            EnsureSuccess(
                await RunAsync("apt-get", ["install", "--yes", "socat"], true, "Install socat", cancellationToken, timeout: PackageTimeout),
                "socat could not be installed");
            var verification = await RunAsync(SocatExecutable, ["-V"], false, "Verify socat installation", cancellationToken, optional: true);
            EnsureSuccess(verification, "socat was installed but its executable is unavailable");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<PortForwardPortCheck> CheckSourcePortAsync(
        PortForwardEditor editor,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var endpoint = NormalizeListenEndpoint(editor);
            var existingPid = editor.Id is { } id
                ? await ReadUnitMainPidAsync(id, cancellationToken)
                : null;
            return await CheckSourcePortInternalAsync(endpoint, existingPid, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SaveAsync(PortForwardEditor editor, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var normalized = NormalizeEditor(editor);
            var interfaces = LoadInterfaceOptions();
            if (interfaces.All(option => !option.Address.Equals(normalized.ListenAddress, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Choose an address that currently belongs to this server, or choose all IPv4 or IPv6 interfaces.");
            }
            var installed = await RunAsync(SocatExecutable, ["-V"], false, "Check socat before saving a port forward", cancellationToken, optional: true);
            EnsureSuccess(installed, "Install socat before creating a port forward");

            var rules = await ReadRulesAsync(cancellationToken);
            var existing = normalized.Id is { } existingId
                ? rules.SingleOrDefault(item => item.Id == existingId)
                : null;
            if (normalized.Id.HasValue && existing is null)
            {
                throw new InvalidOperationException("That port forward no longer exists. Refresh the list and try again.");
            }

            var stored = ToStoredRule(normalized, existing?.Id ?? Guid.NewGuid());
            if (WouldForwardBackToItself(stored, interfaces))
            {
                throw new InvalidOperationException("The destination points back to this same listener and would create a forwarding loop. Choose a different destination address or port.");
            }
            if (stored.IsEnabled)
            {
                var existingPid = existing is null ? null : await ReadUnitMainPidAsync(existing.Id, cancellationToken);
                var availability = await CheckSourcePortInternalAsync(
                    new ListenEndpoint(stored.ListenAddress, stored.SourcePort, stored.Protocol),
                    existingPid,
                    cancellationToken);
                if (!availability.IsAvailable)
                {
                    throw new InvalidOperationException(availability.Summary);
                }
            }

            try
            {
                await InstallUnitAsync(stored, cancellationToken);
                await ReloadSystemdAsync(cancellationToken);
                await ApplyUnitStateAsync(stored, cancellationToken);
                var updated = rules.Where(item => item.Id != stored.Id).Append(stored).ToArray();
                await WriteRulesAsync(updated, cancellationToken);
            }
            catch
            {
                await RestoreUnitAsync(existing, stored.Id, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var rules = await ReadRulesAsync(cancellationToken);
            var existing = rules.SingleOrDefault(item => item.Id == id)
                           ?? throw new InvalidOperationException("That port forward no longer exists.");
            if (existing.IsEnabled == enabled)
            {
                return;
            }

            var updated = existing with { IsEnabled = enabled };
            if (enabled)
            {
                var availability = await CheckSourcePortInternalAsync(
                    new ListenEndpoint(updated.ListenAddress, updated.SourcePort, updated.Protocol),
                    null,
                    cancellationToken);
                if (!availability.IsAvailable)
                {
                    throw new InvalidOperationException(availability.Summary);
                }
            }

            await ApplyUnitStateAsync(updated, cancellationToken);
            await WriteRulesAsync(rules.Where(item => item.Id != id).Append(updated).ToArray(), cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var rules = await ReadRulesAsync(cancellationToken);
            var existing = rules.SingleOrDefault(item => item.Id == id)
                           ?? throw new InvalidOperationException("That port forward no longer exists.");
            await DisableUnitAsync(existing.Id, cancellationToken);
            var remove = await RunAsync(
                "rm",
                ["-f", BuildUnitPath(existing.Id)],
                true,
                $"Remove port-forward service {existing.Name}",
                cancellationToken);
            EnsureSuccess(remove, "The port-forward service file could not be removed");
            await ReloadSystemdAsync(cancellationToken);
            await WriteRulesAsync(rules.Where(item => item.Id != id).ToArray(), cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal static PortForwardEditor NormalizeEditor(PortForwardEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var endpoint = NormalizeListenEndpoint(editor);
        var name = editor.Name.Trim();
        if (name.Length is < 1 or > 80)
        {
            throw new InvalidOperationException("Give the port forward a name from 1 to 80 characters.");
        }

        var destinationText = editor.DestinationAddress.Trim();
        if (!IPAddress.TryParse(destinationText, out var destinationAddress))
        {
            throw new InvalidOperationException("Enter a valid destination IP address.");
        }

        if (destinationAddress.Equals(IPAddress.Any) || destinationAddress.Equals(IPAddress.IPv6Any) ||
            destinationAddress.IsIPv6Multicast ||
            (destinationAddress.AddressFamily == AddressFamily.InterNetwork && destinationAddress.GetAddressBytes()[0] is >= 224 and <= 239))
        {
            throw new InvalidOperationException("Choose a specific unicast destination IP address.");
        }

        if (editor.DestinationPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Choose a destination port from 1 to 65535.");
        }

        return new PortForwardEditor
        {
            Id = editor.Id,
            Name = name,
            ListenAddress = endpoint.Address,
            SourcePort = endpoint.Port,
            Protocol = endpoint.Protocol,
            DestinationAddress = destinationAddress.ToString(),
            DestinationPort = editor.DestinationPort,
            IsEnabled = editor.IsEnabled
        };
    }

    internal static IReadOnlyList<SocketListener> ParseListeners(string output)
    {
        var listeners = new List<SocketListener>();
        foreach (var rawLine in SplitLines(output))
        {
            var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || parts[0] is not "tcp" and not "udp" ||
                !TryParseSocketEndpoint(parts[4], out var address, out var port))
            {
                continue;
            }

            var process = parts.Length > 6 ? string.Join(' ', parts.Skip(6)) : "another process";
            var pidMatch = PidPattern.Match(process);
            listeners.Add(new SocketListener(
                parts[0].Equals("tcp", StringComparison.Ordinal) ? PortForwardProtocol.Tcp : PortForwardProtocol.Udp,
                address,
                port,
                pidMatch.Success && int.TryParse(pidMatch.Groups["pid"].Value, out var pid) ? pid : null,
                FormatProcess(process)));
        }

        return listeners;
    }

    internal static SocketListener? FindConflict(
        ListenEndpoint candidate,
        IEnumerable<SocketListener> listeners,
        int? ignoredPid = null) =>
        listeners.FirstOrDefault(listener =>
            listener.Pid != ignoredPid &&
            listener.Protocol == candidate.Protocol &&
            listener.Port == candidate.Port &&
            AddressesOverlap(candidate.Address, listener.Address));

    internal static IReadOnlyList<string> BuildSocatArguments(StoredPortForward rule)
    {
        var listenAddress = IPAddress.Parse(rule.ListenAddress);
        var destinationAddress = IPAddress.Parse(rule.DestinationAddress);
        var listenFamily = listenAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "6" : "4";
        var destinationFamily = destinationAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "6" : "4";
        var sourceType = rule.Protocol == PortForwardProtocol.Tcp ? $"TCP{listenFamily}-LISTEN" : $"UDP{listenFamily}-RECVFROM";
        var destinationType = rule.Protocol == PortForwardProtocol.Tcp ? $"TCP{destinationFamily}" : $"UDP{destinationFamily}-SENDTO";
        var source = $"{sourceType}:{rule.SourcePort},reuseaddr,fork";
        if (!IsWildcard(rule.ListenAddress))
        {
            source += $",bind={FormatSocatAddress(listenAddress)}";
        }

        var destination = $"{destinationType}:{FormatSocatAddress(destinationAddress)}:{rule.DestinationPort}";
        return ["-d", "-d", source, destination];
    }

    internal static string BuildUnit(StoredPortForward rule)
    {
        var arguments = string.Join(' ', BuildSocatArguments(rule)).Replace("%", "%%", StringComparison.Ordinal);
        return $"""
                [Unit]
                Description=Linux Made Sane managed port forward
                After=network-online.target
                Wants=network-online.target

                [Service]
                Type=simple
                ExecStart={SocatExecutable} {arguments}
                Restart=on-failure
                RestartSec=2
                DynamicUser=true
                NoNewPrivileges=true
                PrivateTmp=true
                ProtectSystem=strict
                ProtectHome=true
                ProtectKernelTunables=true
                ProtectKernelModules=true
                ProtectControlGroups=true
                RestrictAddressFamilies=AF_INET AF_INET6
                CapabilityBoundingSet=CAP_NET_BIND_SERVICE
                AmbientCapabilities=CAP_NET_BIND_SERVICE

                [Install]
                WantedBy=multi-user.target
                """;
    }

    private async Task<PortForwardPortCheck> CheckSourcePortInternalAsync(
        ListenEndpoint endpoint,
        int? ignoredPid,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "ss",
            ["-H", "-ltnup"],
            true,
            "Check listening TCP and UDP ports",
            cancellationToken,
            optional: true);
        if (result.ExitCode != 0)
        {
            return new PortForwardPortCheck(false, $"LMS could not check port availability: {BuildFailureDetail(result)}");
        }

        var conflict = FindConflict(endpoint, ParseListeners(result.StandardOutput), ignoredPid);
        if (conflict is null)
        {
            return new PortForwardPortCheck(
                true,
                $"{FormatProtocol(endpoint.Protocol)} port {endpoint.Port} is free on {FormatListenAddress(endpoint.Address)}.");
        }

        return new PortForwardPortCheck(
            false,
            $"{FormatProtocol(endpoint.Protocol)} port {endpoint.Port} is already listening on {FormatListenAddress(conflict.Address)} ({conflict.Process}). Choose another source port or interface.",
            conflict.Process);
    }

    private async Task InstallUnitAsync(StoredPortForward rule, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"lms-port-forward-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var tempPath = Path.Combine(tempDirectory, BuildUnitName(rule.Id));
            await File.WriteAllTextAsync(tempPath, BuildUnit(rule), cancellationToken);
            var install = await RunAsync(
                "install",
                ["-m", "0644", tempPath, BuildUnitPath(rule.Id)],
                true,
                $"Install port-forward service {rule.Name}",
                cancellationToken);
            EnsureSuccess(install, "The port-forward service file could not be installed");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task ApplyUnitStateAsync(StoredPortForward rule, CancellationToken cancellationToken)
    {
        if (!rule.IsEnabled)
        {
            await DisableUnitAsync(rule.Id, cancellationToken);
            return;
        }

        var enable = await RunAsync(
            "systemctl",
            ["enable", BuildUnitName(rule.Id)],
            true,
            $"Enable port forward {rule.Name}",
            cancellationToken);
        EnsureSuccess(enable, $"The port forward '{rule.Name}' could not be enabled");
        var restart = await RunAsync(
            "systemctl",
            ["restart", BuildUnitName(rule.Id)],
            true,
            $"Start port forward {rule.Name}",
            cancellationToken);
        EnsureSuccess(restart, $"The port forward '{rule.Name}' could not be started");
        var state = await ReadUnitStateAsync(rule.Id, cancellationToken);
        if (!state.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The port forward '{rule.Name}' did not remain active. systemd reports {state}.");
        }
    }

    private async Task DisableUnitAsync(Guid id, CancellationToken cancellationToken)
    {
        var disable = await RunAsync(
            "systemctl",
            ["disable", "--now", BuildUnitName(id)],
            true,
            "Disable managed port forward",
            cancellationToken);
        if (disable.ExitCode != 0 && !disable.StandardError.Contains("does not exist", StringComparison.OrdinalIgnoreCase) &&
            !disable.StandardError.Contains("not loaded", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSuccess(disable, "The port forward could not be disabled");
        }
    }

    private async Task RestoreUnitAsync(StoredPortForward? existing, Guid attemptedId, CancellationToken cancellationToken)
    {
        try
        {
            if (existing is not null)
            {
                await InstallUnitAsync(existing, cancellationToken);
                await ReloadSystemdAsync(cancellationToken);
                await ApplyUnitStateAsync(existing, cancellationToken);
                return;
            }

            await DisableUnitAsync(attemptedId, cancellationToken);
            await RunAsync("rm", ["-f", BuildUnitPath(attemptedId)], true, "Remove failed port-forward service", cancellationToken);
            await ReloadSystemdAsync(cancellationToken);
        }
        catch
        {
            // Preserve the original error; the failed unit remains visible to systemd diagnostics.
        }
    }

    private async Task ReloadSystemdAsync(CancellationToken cancellationToken)
    {
        var reload = await RunAsync("systemctl", ["daemon-reload"], true, "Reload systemd port-forward services", cancellationToken);
        EnsureSuccess(reload, "systemd could not reload the port-forward service definition");
    }

    private async Task<string> ReadUnitStateAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "systemctl",
            ["is-active", BuildUnitName(id)],
            false,
            "Read port-forward service state",
            cancellationToken,
            optional: true);
        return FirstNonEmptyLine(result.StandardOutput, result.StandardError) ?? "inactive";
    }

    private async Task<int?> ReadUnitMainPidAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "systemctl",
            ["show", BuildUnitName(id), "--property=MainPID", "--value", "--no-pager"],
            false,
            "Read port-forward process ID",
            cancellationToken,
            optional: true);
        return result.ExitCode == 0 && int.TryParse(result.StandardOutput.Trim(), out var pid) && pid > 0 ? pid : null;
    }

    private async Task<IReadOnlyList<StoredPortForward>> ReadRulesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RulesPath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(RulesPath, cancellationToken);
            return JsonSerializer.Deserialize<List<StoredPortForward>>(json, JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException("The saved port-forward configuration could not be read.", exception);
        }
    }

    private async Task WriteRulesAsync(IReadOnlyList<StoredPortForward> rules, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.DirectoryPath);
        var temporaryPath = $"{RulesPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(rules, JsonOptions), cancellationToken);
            File.Move(temporaryPath, RulesPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private Task<LinuxCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool requiresSudo,
        string description,
        CancellationToken cancellationToken,
        bool optional = false,
        TimeSpan? timeout = null) =>
        commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, timeout ?? CommandTimeout, description)
            {
                IsOptionalExternalTool = optional
            },
            dryRun: false,
            cancellationToken);

    private static ListenEndpoint NormalizeListenEndpoint(PortForwardEditor editor)
    {
        var listenText = editor.ListenAddress.Trim();
        if (!IPAddress.TryParse(listenText, out var listenAddress))
        {
            throw new InvalidOperationException("Choose a valid local interface address.");
        }

        if (editor.SourcePort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Choose a source port from 1 to 65535.");
        }

        return new ListenEndpoint(listenAddress.ToString(), editor.SourcePort, editor.Protocol);
    }

    private static StoredPortForward ToStoredRule(PortForwardEditor editor, Guid id) => new(
        id,
        editor.Name,
        editor.ListenAddress,
        editor.SourcePort,
        editor.Protocol,
        editor.DestinationAddress,
        editor.DestinationPort,
        editor.IsEnabled);

    private static IReadOnlyList<PortForwardInterfaceOption> LoadInterfaceOptions()
    {
        var options = new List<PortForwardInterfaceOption>
        {
            new("0.0.0.0", "All IPv4 interfaces", "All IPv4 interfaces · 0.0.0.0", true, false),
            new("::", "All IPv6 interfaces", "All IPv6 interfaces · ::", true, true)
        };
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                                    item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses
                         .Select(item => item.Address)
                         .Where(address => !IPAddress.IsLoopback(address) &&
                                           address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                         .OrderBy(address => address.AddressFamily)
                         .ThenBy(address => address.ToString(), StringComparer.Ordinal))
            {
                options.Add(new PortForwardInterfaceOption(
                    address.ToString(),
                    networkInterface.Name,
                    $"{networkInterface.Name} · {address}",
                    false,
                    address.AddressFamily == AddressFamily.InterNetworkV6));
            }
        }

        return options.DistinctBy(option => option.Address, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveInterfaceLabel(string address, IReadOnlyList<PortForwardInterfaceOption> interfaces) =>
        interfaces.FirstOrDefault(option => option.Address.Equals(address, StringComparison.OrdinalIgnoreCase))?.InterfaceName
        ?? address;

    private static bool TryParseSocketEndpoint(string value, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        var separator = value.LastIndexOf(':');
        if (separator < 0 || !int.TryParse(value[(separator + 1)..], out port))
        {
            return false;
        }

        var rawAddress = value[..separator].Trim('[', ']');
        var scopeSeparator = rawAddress.IndexOf('%');
        if (scopeSeparator >= 0)
        {
            rawAddress = rawAddress[..scopeSeparator];
        }

        address = rawAddress is "*" or "0.0.0.0" ? "0.0.0.0" : rawAddress is "::" ? "::" : rawAddress;
        return IPAddress.TryParse(address, out _) && port is >= 1 and <= 65535;
    }

    private static bool AddressesOverlap(string first, string second)
    {
        if (!IPAddress.TryParse(first, out var firstAddress) || !IPAddress.TryParse(second, out var secondAddress))
        {
            return first.Equals(second, StringComparison.OrdinalIgnoreCase);
        }

        if (firstAddress.Equals(secondAddress))
        {
            return true;
        }

        if (IsWildcard(first) || IsWildcard(second))
        {
            if (firstAddress.AddressFamily == secondAddress.AddressFamily)
            {
                return true;
            }

            // Linux can accept IPv4 traffic on an IPv6 wildcard socket unless IPV6_V6ONLY is set.
            return firstAddress.Equals(IPAddress.IPv6Any) || secondAddress.Equals(IPAddress.IPv6Any);
        }

        return false;
    }

    private static bool WouldForwardBackToItself(
        StoredPortForward rule,
        IReadOnlyList<PortForwardInterfaceOption> interfaces)
    {
        if (rule.SourcePort != rule.DestinationPort ||
            !IPAddress.TryParse(rule.ListenAddress, out var listenAddress) ||
            !IPAddress.TryParse(rule.DestinationAddress, out var destinationAddress) ||
            listenAddress.AddressFamily != destinationAddress.AddressFamily)
        {
            return false;
        }

        if (listenAddress.Equals(destinationAddress))
        {
            return true;
        }

        return IsWildcard(rule.ListenAddress) &&
               (IPAddress.IsLoopback(destinationAddress) || interfaces.Any(option =>
                   option.Address.Equals(destinationAddress.ToString(), StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsWildcard(string address) => address is "0.0.0.0" or "::";

    private static string FormatListenAddress(string address) => IsWildcard(address) ? "all interfaces" : address;

    private static string FormatSocatAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static string FormatProtocol(PortForwardProtocol protocol) => protocol == PortForwardProtocol.Tcp ? "TCP" : "UDP";

    private static string BuildUnitName(Guid id) => $"linux-made-sane-port-forward-{id:N}.service";

    private static string BuildUnitPath(Guid id) => Path.Combine(UnitDirectory, BuildUnitName(id));

    private static string FormatProcess(string process)
    {
        var match = Regex.Match(process, "\\(\\(\"(?<name>[^\"]+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value : "another process";
    }

    private static void EnsureSuccess(LinuxCommandResult result, string prefix)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{prefix}: {BuildFailureDetail(result)}");
        }
    }

    private static string BuildFailureDetail(LinuxCommandResult result) =>
        FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}";

    private static string? FirstNonEmptyLine(params string[] values) =>
        values.SelectMany(SplitLines).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    internal sealed record StoredPortForward(
        Guid Id,
        string Name,
        string ListenAddress,
        int SourcePort,
        PortForwardProtocol Protocol,
        string DestinationAddress,
        int DestinationPort,
        bool IsEnabled);

    internal sealed record ListenEndpoint(string Address, int Port, PortForwardProtocol Protocol);

    internal sealed record SocketListener(
        PortForwardProtocol Protocol,
        string Address,
        int Port,
        int? Pid,
        string Process);
}
