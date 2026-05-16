// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public static partial class AiCommandDisplayFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool IsCommandExecutionTool(string toolName) => toolName switch
    {
        AiToolNames.GetServerHealth => true,
        AiToolNames.ListServices => true,
        AiToolNames.RestartService => true,
        AiToolNames.RunCommand => true,
        AiToolNames.InstallPackageWithConfirmation => true,
        _ => false
    };

    public static Guid? ResolveTargetServerId(string toolName, string argumentsJson)
    {
        try
        {
            return toolName switch
            {
                AiToolNames.GetServerSummary => DeserializeArguments<GetServerSummaryToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.GetServerHealth => DeserializeArguments<GetServerHealthToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.ListServices => DeserializeArguments<ListServicesToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.RestartService => DeserializeArguments<RestartServiceToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.BrowseDirectory => DeserializeArguments<BrowseDirectoryToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.ReadFile => DeserializeArguments<ReadFileToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.RunCommand => DeserializeArguments<RunCommandToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.WriteFileWithConfirmation => DeserializeArguments<WriteFileWithConfirmationToolRequest>(argumentsJson)?.ServerId,
                AiToolNames.InstallPackageWithConfirmation => DeserializeArguments<InstallPackageWithConfirmationToolRequest>(argumentsJson)?.ServerId,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string BuildCommandPreview(string toolName, string argumentsJson)
    {
        try
        {
            return toolName switch
            {
                AiToolNames.RunCommand => BuildRunCommandPreview(DeserializeArguments<RunCommandToolRequest>(argumentsJson)),
                AiToolNames.ListServices => BuildListServicesPreview(DeserializeArguments<ListServicesToolRequest>(argumentsJson)),
                AiToolNames.RestartService => BuildRestartServicePreview(DeserializeArguments<RestartServiceToolRequest>(argumentsJson)),
                AiToolNames.InstallPackageWithConfirmation => BuildInstallPackagesPreview(DeserializeArguments<InstallPackageWithConfirmationToolRequest>(argumentsJson)),
                AiToolNames.GetServerHealth => "Collect hostname, uptime, load, memory, and root-disk metrics over SSH.",
                _ => string.Empty
            };
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static string SanitizeDisplayText(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return string.Empty;
        }

        var sanitized = commandText.Trim()
            .Replace("'\"'\"'", "'", StringComparison.Ordinal);

        if (TryUnwrapShellCommand(sanitized, "/bin/sh -lc ", out var script) ||
            TryUnwrapShellCommand(sanitized, "sh -lc ", out script) ||
            TryUnwrapShellCommand(sanitized, "bash -lc ", out script))
        {
            return script;
        }

        return sanitized;
    }

    private static TArguments? DeserializeArguments<TArguments>(string argumentsJson)
        where TArguments : class =>
        JsonSerializer.Deserialize<TArguments>(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            SerializerOptions);

    private static string BuildRunCommandPreview(RunCommandToolRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CommandText))
        {
            return string.Empty;
        }

        var commandText = request.CommandText.Trim();
        return string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? commandText
            : $"cd {FormatArgumentForDisplay(request.WorkingDirectory.Trim())} && {commandText}";
    }

    private static string BuildListServicesPreview(ListServicesToolRequest? request)
    {
        if (request is null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(request.Filter)
            ? "systemctl list-units --type=service --all --plain --no-pager --no-legend"
            : $"systemctl list-units --type=service --all --plain --no-pager --no-legend | grep -i -- {FormatArgumentForDisplay(request.Filter.Trim())}";
    }

    private static string BuildRestartServicePreview(RestartServiceToolRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ServiceName))
        {
            return string.Empty;
        }

        var serviceName = request.ServiceName.Trim();
        var displayServiceName = FormatArgumentForDisplay(serviceName);
        return $"sudo systemctl restart {displayServiceName} && systemctl is-active {displayServiceName}";
    }

    private static string BuildInstallPackagesPreview(InstallPackageWithConfirmationToolRequest? request)
    {
        if (request is null || request.PackageNames.Count == 0)
        {
            return string.Empty;
        }

        return $"sudo apt-get install -y -- {string.Join(" ", request.PackageNames.Select(packageName => FormatArgumentForDisplay(packageName.Trim())))}";
    }

    private static string FormatArgumentForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        var trimmed = value.Trim();
        return DisplaySafePattern().IsMatch(trimmed)
            ? trimmed
            : $"\"{trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static bool TryUnwrapShellCommand(string commandText, string prefix, out string script)
    {
        script = string.Empty;
        if (!commandText.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var wrappedScript = commandText[prefix.Length..].Trim();
        if (wrappedScript.Length < 2 || wrappedScript[0] != '\'' || wrappedScript[^1] != '\'')
        {
            return false;
        }

        script = wrappedScript[1..^1].Replace("'\"'\"'", "'", StringComparison.Ordinal);
        return true;
    }

    [GeneratedRegex("""^[A-Za-z0-9._/@%+=:,?-]+$""", RegexOptions.CultureInvariant)]
    private static partial Regex DisplaySafePattern();
}
