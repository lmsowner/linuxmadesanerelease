// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text;
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public static class RunbookAiHelper
{
    public static int SyncTemplateParametersFromContent(RunbookEditor editor)
    {
        if (editor is null || !editor.IsTemplate)
        {
            return 0;
        }

        var normalizedDefinitions = RunbookTemplateRenderer.NormalizeDefinitions(editor.Parameters);
        var missingTokens = RunbookTemplateRenderer.FindMissingTokens(editor.CommandText, normalizedDefinitions);
        if (missingTokens.Count == 0)
        {
            return 0;
        }

        foreach (var tokenName in missingTokens)
        {
            var kind = InferParameterKind(tokenName);
            editor.Parameters.Add(new RunbookParameterEditor
            {
                Name = tokenName,
                Label = BuildParameterLabel(tokenName),
                Kind = kind,
                Placeholder = GetDefaultPlaceholder(kind),
                HelpText = "Added automatically from a template token in the draft.",
                IsRequired = true
            });
        }

        return missingTokens.Count;
    }

    public static string BuildPrompt(
        RunbookEditor editor,
        IReadOnlyList<ManagedHost> hosts,
        string userPrompt,
        bool includeSecretValues = true)
    {
        var hostNames = ResolveSelectedHostNames(editor, hosts);
        var machineSummary = hostNames.Count == 0
            ? "No machine selected yet"
            : string.Join(", ", hostNames);
        var currentMode = ResolveContentMode(editor);
        var sudoMode = editor.RequiresSudo ? "sudo" : "user";
        var currentDraft = string.IsNullOrWhiteSpace(editor.CommandText)
            ? "(none yet)"
            : MaskSecretParameterValues(editor.CommandText.Trim(), editor.Parameters, includeSecretValues);
        var isInstantiatedTemplate = !editor.IsTemplate && editor.TemplateSourceId.HasValue && editor.Parameters.Count > 0;
        var parameterSummary = BuildParameterSummary(
            editor.Parameters,
            includeValues: isInstantiatedTemplate,
            includeSecretValues: includeSecretValues);

        var builder = new StringBuilder();
        builder.AppendLine("You are helping draft a Linux Made Sane runbook.");
        builder.AppendLine();
        builder.AppendLine("Linux Made Sane runbook rules:");
        builder.AppendLine("- Every runnable runbook is a bash script.");
        builder.AppendLine("- Template runbooks may contain LMS parameter tokens like {{service_name}} or {{folder_path}}.");
        builder.AppendLine("- A concrete runbook created from a template must be a full bash script with no unresolved {{token}} placeholders.");
        builder.AppendLine("- Prefer not to include sudo inside the script unless absolutely necessary because LMS tracks sudo separately.");
        builder.AppendLine("- Treat the current draft as a live working document and revise it based on the user's request instead of starting over unless they ask for a rewrite.");
        builder.AppendLine();
        builder.AppendLine($"Machines: {machineSummary}");
        builder.AppendLine($"Title: {editor.Name.Trim()}");
        builder.AppendLine($"Summary: {editor.Description.Trim()}");
        builder.AppendLine($"Preferred content type: {currentMode}");
        builder.AppendLine($"Execution mode: {sudoMode}");
        builder.AppendLine($"Distribution: {ResolveDistributionMode(editor)}");

        if (editor.IsTemplate)
        {
            builder.AppendLine("Runbook mode: Template");
            builder.AppendLine("Template guidance:");
            builder.AppendLine("- Keep reusable values as LMS tokens using lowercase snake_case inside double braces, for example {{service_name}}.");
            builder.AppendLine("- Reuse existing tokens where possible.");
            builder.AppendLine("- If you introduce a new token, keep the token name descriptive and LMS will add a matching parameter.");
        }
        else if (isInstantiatedTemplate)
        {
            builder.AppendLine("Runbook mode: Runnable created from a template");
            builder.AppendLine("Concrete guidance:");
            builder.AppendLine("- Output a fully concrete bash script.");
            builder.AppendLine("- Do not leave any unresolved LMS template tokens in the final draft.");
        }
        else
        {
            builder.AppendLine("Runbook mode: Runnable");
            builder.AppendLine("Runnable guidance:");
            builder.AppendLine("- Output a fully concrete bash script with no LMS template tokens.");
        }

        builder.AppendLine();
        builder.AppendLine("Current parameters:");
        builder.AppendLine(parameterSummary);
        builder.AppendLine();
        builder.AppendLine("Current draft:");
        builder.AppendLine(currentDraft);
        builder.AppendLine();
        builder.AppendLine("Reply with a short explanation and then exactly one ```bash code block containing the full script.");
        builder.AppendLine("Do not wrap the response in extra Markdown sections.");
        builder.AppendLine();
        builder.AppendLine("User request:");
        builder.AppendLine(userPrompt.Trim());

        return builder.ToString();
    }

    public static string BuildParameterLabel(string tokenName)
    {
        var parts = tokenName
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Length == 0
                ? string.Empty
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant())
            .Where(part => part.Length > 0)
            .ToArray();

        return parts.Length == 0
            ? tokenName
            : string.Join(" ", parts);
    }

    public static RunbookParameterKind InferParameterKind(string tokenName)
    {
        var normalized = RunbookTemplateRenderer.NormalizeParameterName(tokenName).ToLowerInvariant();
        if (normalized.Contains("service", StringComparison.Ordinal))
        {
            return RunbookParameterKind.ServiceName;
        }

        if (normalized.Contains("package", StringComparison.Ordinal))
        {
            return RunbookParameterKind.PackageName;
        }

        if (normalized.Contains("folder", StringComparison.Ordinal) ||
            normalized.Contains("directory", StringComparison.Ordinal) ||
            normalized.Contains("dir", StringComparison.Ordinal))
        {
            return RunbookParameterKind.FolderPath;
        }

        if (normalized.Contains("file", StringComparison.Ordinal))
        {
            return RunbookParameterKind.FilePath;
        }

        if (normalized.Contains("user", StringComparison.Ordinal))
        {
            return RunbookParameterKind.UserName;
        }

        if (normalized.Contains("group", StringComparison.Ordinal))
        {
            return RunbookParameterKind.GroupName;
        }

        if (normalized.Contains("port", StringComparison.Ordinal))
        {
            return RunbookParameterKind.PortNumber;
        }

        if (normalized.Contains("url", StringComparison.Ordinal) ||
            normalized.Contains("uri", StringComparison.Ordinal) ||
            normalized.Contains("endpoint", StringComparison.Ordinal))
        {
            return RunbookParameterKind.Url;
        }

        if (normalized.Contains("host", StringComparison.Ordinal) ||
            normalized.Contains("server", StringComparison.Ordinal))
        {
            return RunbookParameterKind.HostName;
        }

        return RunbookParameterKind.Text;
    }

    public static string GetDefaultPlaceholder(RunbookParameterKind kind) => kind switch
    {
        RunbookParameterKind.SecretText => "secret-value",
        RunbookParameterKind.FilePath => "/var/log/auth.log",
        RunbookParameterKind.FolderPath => "/srv/shares/projects",
        RunbookParameterKind.ServiceName => "nginx",
        RunbookParameterKind.PackageName => "caddy",
        RunbookParameterKind.UserName => "deploy",
        RunbookParameterKind.GroupName => "www-data",
        RunbookParameterKind.HostName => "web-01",
        RunbookParameterKind.PortNumber => "443",
        RunbookParameterKind.Url => "https://example.com",
        _ => "Value"
    };

    public static IReadOnlyList<string> CollectSecretValues(IEnumerable<RunbookParameterEditor>? parameters) =>
        parameters?
            .Where(parameter =>
                parameter.Kind == RunbookParameterKind.SecretText &&
                !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => parameter.Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static IReadOnlyList<string> ResolveSelectedHostNames(RunbookEditor editor, IReadOnlyList<ManagedHost> hosts)
    {
        var selectedHostIds = editor.IsTemplate
            ? editor.HostId == Guid.Empty ? [] : [editor.HostId]
            : editor.SelectedHostIds.Count > 0
                ? editor.SelectedHostIds
                : editor.HostId == Guid.Empty
                    ? []
                    : [editor.HostId];

        return hosts
            .Where(host => selectedHostIds.Contains(host.Id))
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .Select(host => host.Name)
            .ToArray();
    }

    private static string ResolveContentMode(RunbookEditor editor)
    {
        return editor.IsTemplate
            ? "bash template"
            : "bash script";
    }

    private static string ResolveDistributionMode(RunbookEditor editor) =>
        editor.IsTemplate
            ? "Library template"
            : editor.DistributionMode switch
            {
                RunbookDistributionMode.CopyToMachines => "Available on several machines",
                RunbookDistributionMode.LinkAcrossMachines => "Available on several machines",
                _ => "Single machine"
            };

    private static string BuildParameterSummary(
        IReadOnlyList<RunbookParameterEditor> parameters,
        bool includeValues,
        bool includeSecretValues)
    {
        if (parameters.Count == 0)
        {
            return "- None defined.";
        }

        var builder = new StringBuilder();
        foreach (var parameter in parameters)
        {
            var name = RunbookTemplateRenderer.NormalizeParameterName(parameter.Name);
            if (name.Length == 0)
            {
                continue;
            }

            builder.Append("- ");
            builder.Append(name);
            builder.Append(" | ");
            builder.Append(BuildParameterLabel(name));
            builder.Append(" | ");
            builder.Append(parameter.Kind);
            builder.Append(parameter.IsRequired ? " | required" : " | optional");

            var placeholder = string.IsNullOrWhiteSpace(parameter.Placeholder)
                ? GetDefaultPlaceholder(parameter.Kind)
                : parameter.Placeholder.Trim();
            if (!string.IsNullOrWhiteSpace(placeholder))
            {
                builder.Append(" | placeholder: ");
                builder.Append(placeholder);
            }

            if (includeValues)
            {
                builder.Append(" | current value: ");
                if (parameter.Kind == RunbookParameterKind.SecretText && !includeSecretValues)
                {
                    builder.Append(string.IsNullOrWhiteSpace(parameter.Value) ? "(empty)" : "[secret value redacted]");
                }
                else
                {
                    builder.Append(string.IsNullOrWhiteSpace(parameter.Value) ? "(empty)" : parameter.Value.Trim());
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string MaskSecretParameterValues(
        string content,
        IReadOnlyList<RunbookParameterEditor> parameters,
        bool includeSecretValues)
    {
        if (includeSecretValues || string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var masked = content;
        foreach (var secretValue in CollectSecretValues(parameters)
                     .OrderByDescending(value => value.Length))
        {
            masked = masked.Replace(secretValue, "[REDACTED SECRET VALUE]", StringComparison.Ordinal);
        }

        return masked;
    }
}
