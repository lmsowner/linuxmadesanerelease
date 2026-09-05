// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class TailscaleConfigurationSummaryProvider(ILinuxCommandRunner commandRunner)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Tailscale";

    public int SortOrder => 30;

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var reader = new TailscalePeerStatusReader(commandRunner);
        if (!reader.HasActiveInterface())
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "tailscale",
                ["status", "--self", "--json"],
                false,
                TimeSpan.FromSeconds(8),
                "Read the local Tailscale configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return new LmsConfigurationSummary(
                ModuleName,
                result.ExitCode == 127 ? LmsConfigurationSummaryStatus.NotConfigured : LmsConfigurationSummaryStatus.Warning,
                "Private tailnet connectivity",
                [new("Interface", "Available"), new("Connection", "Not ready")],
                result.ExitCode == 127 ? [] : ["The Tailscale interface exists, but LMS could not read its current identity."],
                null,
                SortOrder);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var self = root.TryGetProperty("Self", out var selfElement) ? selfElement : default;
        var hostName = ReadString(self, "HostName") ?? ReadString(self, "DNSName")?.TrimEnd('.') ?? "Connected";
        var dnsName = ReadString(self, "DNSName")?.TrimEnd('.');
        var ipAddress = ReadFirstString(self, "TailscaleIPs") ?? "Not reported";
        var isExitNode = ReadBoolean(self, "ExitNodeOption");
        var usesExitNode = root.TryGetProperty("ExitNodeStatus", out var exitNodeStatus) &&
                           exitNodeStatus.ValueKind == JsonValueKind.Object;

        var items = new List<LmsConfigurationSummaryItem>
        {
            new("Hostname", hostName),
            new("Tailnet IP", ipAddress),
            new("Connection", "Connected"),
            new("Exit node", usesExitNode ? "In use" : isExitNode ? "Offered by this server" : "Disabled")
        };
        if (!string.IsNullOrWhiteSpace(dnsName) && !dnsName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
        {
            items.Insert(1, new LmsConfigurationSummaryItem("Tailnet DNS", dnsName));
        }

        return new LmsConfigurationSummary(
            ModuleName,
            LmsConfigurationSummaryStatus.Configured,
            "Private tailnet connectivity",
            items,
            [],
            null,
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Private tailnet connectivity", [], [], null, SortOrder);

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string? ReadFirstString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
