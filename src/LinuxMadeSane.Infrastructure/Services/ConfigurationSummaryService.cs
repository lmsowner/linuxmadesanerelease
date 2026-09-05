// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ConfigurationSummaryService(
    IEnumerable<ILmsConfigurationSummaryProvider> providers,
    ILogger<ConfigurationSummaryService> logger,
    TimeProvider timeProvider) : IConfigurationSummaryService
{
    public async Task<LmsConfigurationSummarySnapshot> GetSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        var summaries = new List<LmsConfigurationSummary>();
        foreach (var provider in providers.OrderBy(provider => provider.SortOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var summary = await provider.GetConfigurationSummaryAsync(cancellationToken);
                summaries.Add(MakeSafe(summary with
                {
                    ModuleName = provider.ModuleName,
                    SortOrder = provider.SortOrder
                }));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not read the {ModuleName} configuration summary", provider.ModuleName);
                summaries.Add(MakeSafe(new LmsConfigurationSummary(
                    provider.ModuleName,
                    LmsConfigurationSummaryStatus.Error,
                    $"Unable to read {provider.ModuleName} configuration.",
                    [],
                    ["This module could not be summarized. Its full configuration page is still available."],
                    provider.NavigationUrl,
                    provider.SortOrder)));
            }
        }

        return new LmsConfigurationSummarySnapshot(
            timeProvider.GetUtcNow(),
            summaries
                .OrderBy(summary => StatusRank(summary.Status))
                .ThenBy(summary => summary.SortOrder)
                .ThenBy(summary => summary.ModuleName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public string RenderPlainText(LmsConfigurationSummarySnapshot snapshot)
    {
        var safeSnapshot = snapshot with
        {
            Summaries = snapshot.Summaries.Select(MakeSafe).ToArray()
        };
        var builder = new StringBuilder();
        builder.AppendLine("Linux Made Sane - Server Configuration");
        builder.Append("Generated: ")
            .AppendLine(safeSnapshot.GeneratedAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm zzz"));

        foreach (var summary in safeSnapshot.Summaries)
        {
            builder.AppendLine();
            builder.Append(summary.ModuleName)
                .Append(" [")
                .Append(FormatStatus(summary.Status))
                .AppendLine("]");
            foreach (var item in summary.Items)
            {
                builder.Append("  ")
                    .Append(item.Label)
                    .Append(": ")
                    .AppendLine(item.Value);
            }

            foreach (var warning in summary.Warnings)
            {
                builder.Append("  Warning: ").AppendLine(warning);
            }
        }

        return builder.ToString().TrimEnd();
    }

    internal static LmsConfigurationSummary MakeSafe(LmsConfigurationSummary summary) =>
        summary with
        {
            ModuleName = Clean(summary.ModuleName, "Module"),
            Description = Clean(summary.Description, string.Empty),
            Items = summary.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Label))
                .Take(10)
                .Select(item => item with
                {
                    Label = Clean(item.Label, "Setting"),
                    Value = item.IsSensitive
                        ? string.IsNullOrWhiteSpace(item.Value) ? "Not configured" : "Configured"
                        : Clean(item.Value, "Not configured")
                })
                .ToArray(),
            Warnings = summary.Warnings
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Take(3)
                .Select(warning => Clean(warning, string.Empty))
                .ToArray(),
            NavigationUrl = NormalizeNavigationUrl(summary.NavigationUrl)
        };

    private static int StatusRank(LmsConfigurationSummaryStatus status) => status switch
    {
        LmsConfigurationSummaryStatus.Configured => 0,
        LmsConfigurationSummaryStatus.Warning => 1,
        LmsConfigurationSummaryStatus.Error => 2,
        LmsConfigurationSummaryStatus.Disabled => 3,
        _ => 4
    };

    private static string FormatStatus(LmsConfigurationSummaryStatus status) => status switch
    {
        LmsConfigurationSummaryStatus.NotConfigured => "Not Configured",
        _ => status.ToString()
    };

    private static string Clean(string? value, string fallback)
    {
        var printable = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        var clean = string.Join(
            ' ',
            printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(clean)
            ? fallback
            : clean.Length <= 500 ? clean : clean[..500] + "…";
    }

    private static string? NormalizeNavigationUrl(string? value)
    {
        var clean = Clean(value, string.Empty);
        return clean.StartsWith("/", StringComparison.Ordinal) && !clean.StartsWith("//", StringComparison.Ordinal)
            ? clean
            : null;
    }
}
