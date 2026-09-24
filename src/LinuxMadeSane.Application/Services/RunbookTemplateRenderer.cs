// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public static partial class RunbookTemplateRenderer
{
    [GeneratedRegex("\\{\\{\\s*([a-zA-Z][a-zA-Z0-9_]*)\\s*\\}\\}", RegexOptions.Compiled)]
    private static partial Regex TemplateTokenRegex();

    public static IReadOnlyList<RunbookParameterDefinition> NormalizeDefinitions(IEnumerable<RunbookParameterEditor>? parameters)
    {
        var items = new List<RunbookParameterDefinition>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters ?? [])
        {
            var normalizedName = NormalizeParameterName(parameter.Name);
            if (normalizedName.Length == 0 || !seenNames.Add(normalizedName))
            {
                continue;
            }

            items.Add(new RunbookParameterDefinition(
                normalizedName,
                string.IsNullOrWhiteSpace(parameter.Label) ? normalizedName : parameter.Label.Trim(),
                parameter.Kind,
                parameter.Placeholder?.Trim() ?? string.Empty,
                parameter.HelpText?.Trim() ?? string.Empty,
                parameter.IsRequired));
        }

        return items;
    }

    public static IReadOnlyDictionary<string, string> NormalizeValueSnapshot(IEnumerable<RunbookParameterEditor>? parameters)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters ?? [])
        {
            var normalizedName = NormalizeParameterName(parameter.Name);
            if (normalizedName.Length == 0 || items.ContainsKey(normalizedName))
            {
                continue;
            }

            items[normalizedName] = parameter.Value?.Trim() ?? string.Empty;
        }

        return items;
    }

    public static string Render(string template, IReadOnlyList<RunbookParameterDefinition> definitions, IReadOnlyDictionary<string, string> values)
    {
        var normalizedTemplate = NormalizeContent(template);
        if (normalizedTemplate.Length == 0)
        {
            return string.Empty;
        }

        var definitionLookup = definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
        return TemplateTokenRegex().Replace(normalizedTemplate, match =>
        {
            var parameterName = NormalizeParameterName(match.Groups[1].Value);
            if (!definitionLookup.TryGetValue(parameterName, out var definition))
            {
                throw new InvalidOperationException($"Runbook parameter '{parameterName}' is not defined.");
            }

            values.TryGetValue(parameterName, out var rawValue);
            var normalizedValue = rawValue?.Trim() ?? string.Empty;
            if (definition.IsRequired && normalizedValue.Length == 0)
            {
                throw new InvalidOperationException($"Runbook parameter '{definition.Label}' is required.");
            }

            return QuoteValue(definition.Kind, normalizedValue);
        });
    }

    public static IReadOnlyList<string> FindMissingTokens(string template, IReadOnlyList<RunbookParameterDefinition> definitions)
    {
        var normalizedDefinitions = definitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in TemplateTokenRegex().Matches(template ?? string.Empty))
        {
            var parameterName = NormalizeParameterName(match.Groups[1].Value);
            if (parameterName.Length == 0 || normalizedDefinitions.Contains(parameterName) || !seen.Add(parameterName))
            {
                continue;
            }

            missing.Add(parameterName);
        }

        return missing;
    }

    public static string NormalizeParameterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var normalized = new string(name.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsLetter(normalized[0]))
        {
            normalized = $"param_{normalized}";
        }

        return normalized;
    }

    public static string BuildToken(string? name)
    {
        var normalizedName = NormalizeParameterName(name);
        return normalizedName.Length == 0 ? "{{parameter_name}}" : $"{{{{{normalizedName}}}}}";
    }

    public static string NormalizeContent(string content) =>
        (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string QuoteValue(RunbookParameterKind kind, string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return kind switch
        {
            RunbookParameterKind.RawText => value,
            RunbookParameterKind.PortNumber when int.TryParse(value, out _) => value,
            _ => ShellQuote(value)
        };
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
