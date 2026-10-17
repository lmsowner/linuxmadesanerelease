// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
        var currentMode = "bash script";
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
        builder.AppendLine($"Run against: {editor.TargetKind}");
        if (editor.TargetKind != RunbookTargetKind.Machine)
        {
            builder.AppendLine("LMS passes selected absolute paths as bash positional arguments. Use \"$1\" for one file or folder; use for file in \"$@\"; do ...; done for selected files. Never hardcode input paths, use eval, or split filenames on whitespace.");
            builder.AppendLine("For folder runbooks, operate within \"$1\"; recurse only when requested. Check required tools, report missing packages clearly, preserve source files and avoid overwriting existing output unless explicitly requested.");
        }
        builder.AppendLine("Linux Made Sane runbook rules:");
        builder.AppendLine("- Every runnable runbook is a bash script.");
        builder.AppendLine("- Check prerequisites on the actual target machine before doing work: executable availability, configuration, permissions, services and input paths. Never assume tools on the authoring machine exist on another host.");
        builder.AppendLine("- Keep standard check_prerequisites, prepare, process and finish hooks. Populate required_commands with every external executable used. Validate every selected input before changing any file.");
        builder.AppendLine("- Missing dependencies must produce actionable installation guidance and stop before side effects. If the user requests automatic installation, detect the target package manager and map commands to actual package names; install only the requested prerequisites, honour the execution privilege mode, and verify them again.");
        builder.AppendLine($"Input contract: {RunbookScaffold.Describe(editor.TargetKind)}");
        builder.AppendLine("- Produce one complete Bash script; do not introduce a template or token parameter definitions.");
        builder.AppendLine("- Prefer not to include sudo inside the script unless absolutely necessary because LMS tracks sudo separately.");
        builder.AppendLine("- Treat the current draft as a live working document and revise it based on the user's request instead of starting over unless they ask for a rewrite.");
        builder.AppendLine();
        builder.AppendLine($"Machines: {machineSummary}");
        builder.AppendLine($"Title: {editor.Name.Trim()}");
        builder.AppendLine($"Summary: {editor.Description.Trim()}");
        builder.AppendLine($"Preferred content type: {currentMode}");
        builder.AppendLine($"Execution mode: {sudoMode}");
        builder.AppendLine($"Distribution: {ResolveDistributionMode(editor)}");

        builder.AppendLine("Runbook mode: Bash script");
        builder.AppendLine("- Do not leave any unresolved LMS template tokens in the final draft.");
        builder.AppendLine("- Use positional path arguments ($1 or $@), not a templating system. Distinguish files and directories with -f and -d.");
        builder.AppendLine("- The file browser, scheduler and terminal supply the same path arguments. Never prompt during scheduled or batch execution.");

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

    public static bool TryExtractScriptDraft(string? content, out string script)
    {
        script = string.Empty;
        if (!TryExtractCodeBlock(content, out var language, out var block))
        {
            return false;
        }

        if (!language.Contains("bash", StringComparison.OrdinalIgnoreCase) &&
            !language.Contains("sh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        script = RunbookExecutionCommandBuilder.NormalizeStoredScript(block);
        return script.Length > 0;
    }

    public static bool TryExtractCommandDraft(string? content, out string command)
    {
        command = string.Empty;
        if (TryExtractCodeBlock(content, out var language, out var block))
        {
            var normalizedBlock = block.Trim();
            if (normalizedBlock.Length > 0 &&
                normalizedBlock.IndexOf('\n') < 0 &&
                (language.Length == 0 ||
                 language.Contains("sh", StringComparison.OrdinalIgnoreCase) ||
                 language.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
                 language.Contains("bash", StringComparison.OrdinalIgnoreCase)))
            {
                command = normalizedBlock;
                return true;
            }
        }

        var inlineCodeStart = content?.IndexOf('`') ?? -1;
        if (inlineCodeStart >= 0)
        {
            var inlineCodeEnd = content!.IndexOf('`', inlineCodeStart + 1);
            if (inlineCodeEnd > inlineCodeStart + 1)
            {
                var inline = content[(inlineCodeStart + 1)..inlineCodeEnd].Trim();
                if (inline.Length > 0 && inline.IndexOf('\n') < 0)
                {
                    command = inline;
                    return true;
                }
            }
        }

        var firstLine = content?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstLine) && firstLine!.IndexOf('\n') < 0)
        {
            command = firstLine.Trim();
            return true;
        }

        return false;
    }

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

    private static bool TryExtractCodeBlock(string? content, out string language, out string block)
    {
        language = string.Empty;
        block = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var start = content.IndexOf("```", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var languageStart = start + 3;
        var languageEnd = content.IndexOf('\n', languageStart);
        if (languageEnd < 0)
        {
            return false;
        }

        var end = content.IndexOf("```", languageEnd + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        language = content[languageStart..languageEnd].Trim();
        block = content[(languageEnd + 1)..end];
        return true;
    }
}
