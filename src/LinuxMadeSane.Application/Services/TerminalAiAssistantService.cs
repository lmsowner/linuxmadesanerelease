// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public sealed partial class TerminalAiAssistantService(
    IAiProviderRegistry providerRegistry,
    IAiPromptSanitizer promptSanitizer) : ITerminalAiAssistantService
{
    private const int MaxTerminalOutputChars = 3200;
    private const int MaxCustomProviderTerminalOutputChars = 1200;
    private const int MaxRequestChars = 1800;
    private const int MaxHistoryEntries = 12;
    private const int MaxHistoryRecapChars = 1800;
    private const int MaxHistoryEntryChars = 260;

    public async Task<TerminalAiTurnResult> ExecutePromptAsync(
        TerminalAiConversationState conversation,
        TerminalAiPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(request);

        var provider = await ResolveProviderAsync(conversation, cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");

        var now = DateTimeOffset.UtcNow;
        var conversationRecap = BuildConversationRecap(conversation);
        var userPrompt = BuildPrompt(request, conversationRecap, provider.Settings.ProviderType);
        var sanitization = promptSanitizer.Sanitize(userPrompt, provider.Settings.ProviderType);
        var userEntry = new TerminalAiConversationEntry(AiChatMessageRole.User, BuildDisplayUserMessage(request), now);
        var thread = new AiChatThread(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.HostName) ? "Terminal assistant" : $"Terminal · {request.HostName.Trim()}",
            provider.ProviderKey,
            provider.Definition.ProviderType,
            string.IsNullOrWhiteSpace(conversation.ModelId) ? provider.Settings.DefaultModelId : conversation.ModelId,
            LinuxMadeSane.Core.Models.Ai.AiTrustProfile.CreatePreset(AiTrustLevel.Guided),
            conversation.ProviderConversationReference,
            conversation.ProviderResponseId,
            now,
            now);

        AiProviderInputItem[] inputItems =
        [
            new AiProviderMessageInputItem(AiChatMessageRole.User, sanitization.Content)
        ];

        if (ShouldUseImmediateCommandFallback(provider.Settings.ProviderType) && (
                TryBuildTerminalErrorFallback(request, out var immediateFallback) ||
                TryBuildFallbackCommand(request, conversationRecap, out immediateFallback)))
        {
            var immediateAssistantEntry = new TerminalAiConversationEntry(
                AiChatMessageRole.Assistant,
                immediateFallback.Message,
                now,
                immediateFallback.Command);

            return new TerminalAiTurnResult(
                provider.ProviderKey,
                provider.Settings.DisplayName,
                thread.ModelId,
                conversation.ProviderConversationReference,
                conversation.ProviderResponseId,
                sanitization.Summary,
                userEntry,
                immediateAssistantEntry);
        }

        var attachedServers = request.HostId == Guid.Empty
            ? Array.Empty<AiAttachedServer>()
            :
            [
                new AiAttachedServer(
                    Guid.NewGuid(),
                    thread.Id,
                    request.HostId,
                    string.IsNullOrWhiteSpace(request.HostName) ? "Current terminal host" : request.HostName.Trim(),
                    string.IsNullOrWhiteSpace(request.HostAddress) ? request.HostName.Trim() : request.HostAddress.Trim(),
                    "Terminal",
                    now)
            ];

        var result = await provider.ExecuteTurnAsync(
            new AiProviderTurnRequest(
                thread,
                Array.Empty<AiChatMessage>(),
                attachedServers,
                inputItems,
                Array.Empty<AiToolDefinition>(),
                false,
                request.AllowInternetResearch),
            cancellationToken: cancellationToken);

        var assistantText = string.Join(Environment.NewLine + Environment.NewLine,
            result.AssistantOutputs
                .Select(output => output.Content?.Trim())
                .Where(content => !string.IsNullOrWhiteSpace(content)));

        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new InvalidOperationException("The AI provider returned no assistant text.");
        }

        var suggestedCommand = ExtractSuggestedCommand(assistantText);
        if (string.IsNullOrWhiteSpace(suggestedCommand) && (
                TryBuildTerminalErrorFallback(request, out var fallback) ||
                TryBuildFallbackCommand(request, conversationRecap, out fallback)))
        {
            suggestedCommand = fallback.Command;
            assistantText = fallback.Message;
        }

        var assistantEntry = new TerminalAiConversationEntry(
            AiChatMessageRole.Assistant,
            assistantText,
            now,
            suggestedCommand);

        return new TerminalAiTurnResult(
            provider.ProviderKey,
            provider.Settings.DisplayName,
            string.IsNullOrWhiteSpace(result.ModelId) ? thread.ModelId : result.ModelId,
            result.ConversationReference ?? string.Empty,
            result.ProviderResponseId,
            sanitization.Summary,
            userEntry,
            assistantEntry);
    }

    private async Task<IAiProvider?> ResolveProviderAsync(
        TerminalAiConversationState conversation,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(conversation.ProviderKey))
        {
            return await providerRegistry.GetProviderAsync(conversation.ProviderKey, cancellationToken);
        }

        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var runtimeReadyProviders = configuredProviders
            .Where(provider =>
                provider.IsEnabled &&
                providerRegistry.FindDefinition(provider.ProviderType)?.IsRuntimeImplemented != false)
            .ToArray();
        var selectedProvider = runtimeReadyProviders.FirstOrDefault(provider => provider.IsDefault)
            ?? runtimeReadyProviders.FirstOrDefault();

        return selectedProvider is null
            ? null
            : await providerRegistry.GetProviderAsync(selectedProvider.ProviderKey, cancellationToken);
    }

    private static string BuildPrompt(
        TerminalAiPromptRequest request,
        string conversationRecap,
        AiProviderType providerType)
    {
        var hostLabel = string.IsNullOrWhiteSpace(request.HostName)
            ? "this host"
            : request.HostName.Trim();
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? "Unknown"
            : request.WorkingDirectory.Trim();
        var requestText = TrimRequestText(request.Request);
        var outputTail = TrimTerminalOutput(request.TerminalOutput, providerType);

        var builder = new StringBuilder();
        builder.AppendLine("You are helping with an ongoing terminal support conversation.");
        builder.AppendLine("Preserve context from earlier turns and build on what is already known.");
        builder.AppendLine("Do not ask the user to paste large terminal output manually back into chat. If fresh evidence is needed, propose the single next command and assume the interface can run it and analyze the updated terminal session.");
        builder.AppendLine("Do not say 'if you want I can', 'you could try', or similar hedging. State the next fix, check, or command directly.");
        builder.AppendLine("When a command is appropriate, include exactly one fenced bash block so the interface can offer Run then review or Run commands.");
        builder.AppendLine("If the operator asks for live host state, counts, lists, running processes, services, Docker containers, packages, ports, devices, or current status, do not invent the answer. Return the single command that collects the evidence.");
        builder.AppendLine("If recent terminal output shows permission denied for the previous command, do not repeat the same unprivileged command. Retry the same focused check with sudo when that is the minimum safe next step.");
        if (providerType == AiProviderType.Custom)
        {
            builder.AppendLine("This provider may be a small Docker-hosted OpenAI-compatible model. Keep the answer deterministic: one short sentence plus one fenced bash block whenever terminal evidence is needed.");
        }
        if (request.AllowInternetResearch)
        {
            builder.AppendLine("Internet research is allowed for this turn. If local terminal evidence is not enough, use web search selectively to find concrete answers and fold the findings into the diagnosis or fix.");
        }

        builder.AppendLine(request.Mode switch
        {
            TerminalAiPromptMode.Analyze => $"Review the current terminal state on {hostLabel}. Explain what is happening, note risks, and suggest the safest next steps. Do not assume commands already succeeded unless the terminal output confirms it. Prefer explanation and diagnosis over command spam.",
            TerminalAiPromptMode.SuggestCommand => $"Suggest the single best next command to run in the current terminal session on {hostLabel}. Use the ongoing conversation context. Return the safest next command only and explain briefly why it is the next fix.",
            TerminalAiPromptMode.Investigate => $"Investigate the issue on {hostLabel}. Continue until you identify the likely cause or you have exhausted the next focused step. Prefer diagnostic commands first, but if a fix command is required to confirm the cause or complete the investigation, use only the minimum next command. Return at most one command at a time. Once you have the likely cause, explain it clearly and suggest the fix.",
            _ => $"Use this terminal context on {hostLabel} to help with the operator request. If the operator asks a question, answer it directly. If the operator gives an instruction, carry it out with the minimum safe next step. Default to concise diagnosis plus the next action. If evidence is missing, ask one focused follow-up question or give the single best next check instead of a list of random commands."
        });
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(conversationRecap))
        {
            builder.AppendLine("Recent conversation context:");
            builder.AppendLine(conversationRecap);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(requestText))
        {
            builder.AppendLine($"Operator request: {requestText}");
            builder.AppendLine();
        }

        builder.AppendLine($"Current directory: {workingDirectory}");
        builder.AppendLine();
        builder.AppendLine("Recent terminal output:");
        builder.AppendLine(outputTail);

        return builder.ToString().Trim();
    }

    private static string BuildDisplayUserMessage(TerminalAiPromptRequest request)
    {
        var requestText = TrimRequestText(request.Request);
        if (!string.IsNullOrWhiteSpace(requestText))
        {
            return requestText;
        }

        return request.Mode switch
        {
            TerminalAiPromptMode.Analyze => "What does this mean?",
            TerminalAiPromptMode.SuggestCommand => "Next command",
            TerminalAiPromptMode.Investigate => "Deep Fix",
            _ => "Help with the current terminal session"
        };
    }

    private static string BuildConversationRecap(TerminalAiConversationState conversation)
    {
        if (conversation.Entries.Count == 0)
        {
            return string.Empty;
        }

        var lines = conversation.Entries
            .TakeLast(MaxHistoryEntries)
            .Select(entry =>
            {
                var content = SummarizeHistoryEntry(entry.Content);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return string.Empty;
                }

                var speaker = entry.Role switch
                {
                    AiChatMessageRole.Assistant => "AI",
                    AiChatMessageRole.System => "System",
                    _ => "User"
                };

                return $"{speaker}: {content}";
            })
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var recap = string.Join(Environment.NewLine, lines);
        if (recap.Length <= MaxHistoryRecapChars)
        {
            return recap;
        }

        return $"...{recap[^MaxHistoryRecapChars..]}";
    }

    private static string SummarizeHistoryEntry(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = MultiWhitespacePattern().Replace(content.Trim(), " ");
        if (normalized.Length <= MaxHistoryEntryChars)
        {
            return normalized;
        }

        return $"{normalized[..MaxHistoryEntryChars]}...";
    }

    private static string TrimTerminalOutput(string terminalOutput, AiProviderType providerType)
    {
        if (string.IsNullOrWhiteSpace(terminalOutput))
        {
            return "(No terminal output captured yet)";
        }

        var normalized = terminalOutput.Trim();
        var maxChars = providerType == AiProviderType.Custom
            ? MaxCustomProviderTerminalOutputChars
            : MaxTerminalOutputChars;
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return $"[recent output tail]{Environment.NewLine}{normalized[^maxChars..]}";
    }

    private static string TrimRequestText(string requestText)
    {
        if (string.IsNullOrWhiteSpace(requestText))
        {
            return string.Empty;
        }

        var normalized = requestText.Trim();
        if (normalized.Length <= MaxRequestChars)
        {
            return normalized;
        }

        return $"{normalized[..MaxRequestChars]}...";
    }

    private static string ExtractSuggestedCommand(string assistantText)
    {
        foreach (Match match in SuggestedCommandPattern().Matches(assistantText))
        {
            var command = ExtractLikelyCommand(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(command))
            {
                return command;
            }
        }

        foreach (var line in assistantText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var command = NormalizePotentialCommand(line);
            if (IsLikelyShellCommand(command))
            {
                return command;
            }
        }

        var inlineMatch = InlineBacktickCommandPattern().Match(assistantText);
        return inlineMatch.Success && IsLikelyShellCommand(inlineMatch.Groups[1].Value.Trim())
            ? inlineMatch.Groups[1].Value.Trim()
            : string.Empty;
    }

    private static string ExtractLikelyCommand(string value)
    {
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var command = NormalizePotentialCommand(line);
            if (IsLikelyShellCommand(command))
            {
                return command;
            }
        }

        var normalized = NormalizePotentialCommand(value);
        return IsLikelyShellCommand(normalized) ? normalized : string.Empty;
    }

    private static bool TryBuildFallbackCommand(
        TerminalAiPromptRequest request,
        string conversationRecap,
        out TerminalCommandFallback fallback)
    {
        var context = $"{request.Request}{Environment.NewLine}{conversationRecap}".ToLowerInvariant();
        fallback = default;

        if (!LooksLikeLiveStateRequest(context) &&
            request.Mode is not TerminalAiPromptMode.SuggestCommand and not TerminalAiPromptMode.Investigate)
        {
            return false;
        }

        var command = ResolveLiveStateCommand(context);
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        fallback = new TerminalCommandFallback(
            command,
            BuildFallbackMessage(command, context));
        return true;
    }

    private static bool ShouldUseImmediateCommandFallback(AiProviderType providerType) =>
        providerType is AiProviderType.Custom or AiProviderType.Ollama;

    private static bool TryBuildTerminalErrorFallback(
        TerminalAiPromptRequest request,
        out TerminalCommandFallback fallback)
    {
        fallback = default;
        if (string.IsNullOrWhiteSpace(request.TerminalOutput))
        {
            return false;
        }

        var terminalOutput = StripAnsi(request.TerminalOutput);
        var lowerOutput = terminalOutput.ToLowerInvariant();
        if (!ContainsAny(lowerOutput, "permission denied", "operation not permitted"))
        {
            return false;
        }

        var deniedCommand = ExtractLastPromptCommandBeforeError(terminalOutput);
        if (string.IsNullOrWhiteSpace(deniedCommand))
        {
            return false;
        }

        var retryCommand = BuildSudoRetryCommand(deniedCommand, lowerOutput);
        if (string.IsNullOrWhiteSpace(retryCommand))
        {
            return false;
        }

        fallback = new TerminalCommandFallback(
            retryCommand,
            BuildPermissionDeniedFallbackMessage(retryCommand, lowerOutput));
        return true;
    }

    private static string ResolveLiveStateCommand(string context)
    {
        if (ContainsAny(context, "docker", "container", "containers", "compose"))
        {
            return context.Contains("stopped", StringComparison.Ordinal) ||
                   context.Contains("all container", StringComparison.Ordinal)
                ? "docker ps -a --format 'table {{.Names}}\\t{{.Image}}\\t{{.Status}}\\t{{.Ports}}'"
                : "docker ps --format 'table {{.Names}}\\t{{.Image}}\\t{{.Status}}\\t{{.Ports}}'";
        }

        if (ContainsAny(context, "process", "processes", "ram", "memory", "cpu usage"))
        {
            return "ps -eo pid,user,comm,%mem,%cpu,rss --sort=-rss | head -n 25";
        }

        if (ContainsAny(context, "bluetooth"))
        {
            return "bluetoothctl devices";
        }

        if (ContainsAny(context, "listening", "open port", "ports", "socket", "sockets"))
        {
            return "ss -tulpn";
        }

        if (ContainsAny(context, "service", "services", "systemd", "daemon"))
        {
            return "systemctl --type=service --state=running --no-pager";
        }

        if (ContainsAny(context, "disk", "storage", "space", "filesystem", "file system"))
        {
            return "df -hT";
        }

        if (ContainsAny(context, "tailscale"))
        {
            return "tailscale status";
        }

        if (ContainsAny(context, "network", "interface", "interfaces", "ip address", "addresses", "route", "gateway"))
        {
            return "ip -brief address && ip route";
        }

        if (ContainsAny(context, "journal", "logs", "log entries"))
        {
            return "journalctl -p warning -n 80 --no-pager";
        }

        if (ContainsAny(context, "gpu", "nvidia", "graphics"))
        {
            return "nvidia-smi || lspci | grep -Ei 'vga|3d|display'";
        }

        if (ContainsAny(context, "package", "packages", "apt", "dpkg"))
        {
            return "apt list --installed 2>/dev/null | head -n 50";
        }

        return string.Empty;
    }

    private static string BuildFallbackMessage(string command, string context)
    {
        if (command.StartsWith("docker ", StringComparison.Ordinal))
        {
            return "Run this to get the live Docker container list; I will review the output after it runs.";
        }

        if (context.Contains("process", StringComparison.Ordinal) ||
            context.Contains("ram", StringComparison.Ordinal) ||
            context.Contains("memory", StringComparison.Ordinal))
        {
            return "Run this to get the live process view; I will review the output after it runs.";
        }

        return "Run this to collect the live terminal evidence; I will review the output after it runs.";
    }

    private static string ExtractLastPromptCommandBeforeError(string terminalOutput)
    {
        var lines = terminalOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errorIndex = Array.FindLastIndex(
            lines,
            line => line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase));
        if (errorIndex <= 0)
        {
            return string.Empty;
        }

        for (var index = errorIndex - 1; index >= 0; index--)
        {
            var command = ExtractCommandFromPromptLine(lines[index]);
            if (!string.IsNullOrWhiteSpace(command))
            {
                return command;
            }
        }

        return string.Empty;
    }

    private static string ExtractCommandFromPromptLine(string line)
    {
        var trimmed = line.Trim();
        var dollarIndex = trimmed.LastIndexOf("$ ", StringComparison.Ordinal);
        var hashIndex = trimmed.LastIndexOf("# ", StringComparison.Ordinal);
        var promptIndex = Math.Max(dollarIndex, hashIndex);
        if (promptIndex < 0 || promptIndex + 2 >= trimmed.Length)
        {
            return string.Empty;
        }

        return trimmed[(promptIndex + 2)..].Trim();
    }

    private static string BuildSudoRetryCommand(string command, string lowerTerminalOutput)
    {
        var normalized = command.Trim();
        if (normalized.StartsWith("sudo ", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (StartsWithShellCommand(normalized, "docker") &&
            lowerTerminalOutput.Contains("/var/run/docker.sock", StringComparison.Ordinal))
        {
            return $"sudo {normalized}";
        }

        return IsReadOnlySudoRetryCommand(normalized)
            ? $"sudo {normalized}"
            : string.Empty;
    }

    private static bool IsReadOnlySudoRetryCommand(string command)
    {
        if (StartsWithShellCommand(command, "journalctl") ||
            StartsWithShellCommand(command, "ss") ||
            StartsWithShellCommand(command, "lsof") ||
            StartsWithShellCommand(command, "nft") && command.Contains(" list ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!StartsWithShellCommand(command, "systemctl"))
        {
            return false;
        }

        var lowerCommand = command.ToLowerInvariant();
        return ContainsAny(
            lowerCommand,
            " status",
            " show",
            " cat ",
            " list-",
            " is-active",
            " --type=",
            " --state=");
    }

    private static string BuildPermissionDeniedFallbackMessage(string command, string lowerTerminalOutput)
    {
        if (command.StartsWith("sudo docker ", StringComparison.Ordinal) &&
            lowerTerminalOutput.Contains("/var/run/docker.sock", StringComparison.Ordinal))
        {
            return "Docker is reachable, but this terminal user cannot read /var/run/docker.sock. Run the same read-only Docker check with sudo so I can review the output.";
        }

        return "The previous command was blocked by permissions. Retry the same focused check with sudo so I can review the output.";
    }

    private static bool StartsWithShellCommand(string command, string executable)
    {
        var trimmed = command.TrimStart();
        return trimmed.Equals(executable, StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith($"{executable} ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLiveStateRequest(string context) =>
        ContainsAny(
            context,
            "how many",
            "show me",
            "show the",
            "show them",
            "list",
            "view",
            "running",
            "what is running",
            "what are running",
            "status",
            "check",
            "find",
            "current");

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));

    private static string NormalizePotentialCommand(string line)
    {
        var command = line.Trim();
        if (command.StartsWith("$ ", StringComparison.Ordinal) ||
            command.StartsWith("# ", StringComparison.Ordinal))
        {
            command = command[2..].Trim();
        }

        if (command.StartsWith("sudo ", StringComparison.OrdinalIgnoreCase))
        {
            return command;
        }

        var colonIndex = command.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex >= 0 && colonIndex < 24)
        {
            command = command[(colonIndex + 1)..].Trim();
        }

        return command;
    }

    private static bool IsLikelyShellCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            command.Contains('\n') ||
            command.Contains('\r') ||
            command.EndsWith(".", StringComparison.Ordinal) ||
            command.Contains(" should ", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" can ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokens = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenIndex = 0;
        while (tokenIndex < tokens.Length && IsCommandPrefixToken(tokens[tokenIndex]))
        {
            tokenIndex++;
        }

        while (tokenIndex < tokens.Length && IsEnvironmentAssignment(tokens[tokenIndex]))
        {
            tokenIndex++;
        }

        if (tokenIndex >= tokens.Length)
        {
            return false;
        }

        var executable = tokens[tokenIndex].Trim();
        return IsExecutableToken(executable) &&
               !IsProseLeadToken(executable) &&
               HasShellCommandShape(tokens, tokenIndex);
    }

    private static bool IsCommandPrefixToken(string token) =>
        token.Equals("sudo", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("doas", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("env", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("command", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("time", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("run_with_optional_sudo", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnvironmentAssignment(string token)
    {
        var separatorIndex = token.IndexOf('=', StringComparison.Ordinal);
        return separatorIndex > 0 &&
               separatorIndex < token.Length - 1 &&
               token[..separatorIndex].All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static bool IsExecutableToken(string token) =>
        token.Length > 0 &&
        token.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or '+');

    private static bool IsProseLeadToken(string token)
    {
        var normalized = token.TrimStart('.', '/', '~').Trim().ToLowerInvariant();
        return normalized is
            "a" or "an" or "and" or "because" or "but" or "for" or "i" or "if" or "it" or "not" or "no" or "now" or
            "please" or "run" or "so" or "that" or "the" or "then" or "there" or "this" or "to" or "try" or "use" or
            "we" or "what" or "when" or "where" or "why" or "you";
    }

    private static bool HasShellCommandShape(IReadOnlyList<string> tokens, int executableIndex)
    {
        if (tokens[executableIndex].Contains('/', StringComparison.Ordinal) ||
            tokens[executableIndex].StartsWith("./", StringComparison.Ordinal) ||
            tokens[executableIndex].StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        if (tokens.Count <= executableIndex + 1)
        {
            return IsCommonSingleTokenCommand(tokens[executableIndex]);
        }

        return true;
    }

    private static bool IsCommonSingleTokenCommand(string executable)
    {
        var commandName = Path.GetFileName(executable);
        return commandName is
            "date" or "df" or "free" or "hostname" or "id" or "ls" or "pwd" or "reboot" or "top" or "uptime" or
            "whoami";
    }

    [GeneratedRegex("```(?:bash|sh)?\\s*\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuggestedCommandPattern();

    [GeneratedRegex("`([^`\\r\\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineBacktickCommandPattern();

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespacePattern();

    private static string StripAnsi(string value) =>
        AnsiEscapePattern().Replace(value, string.Empty);

    private readonly record struct TerminalCommandFallback(string Command, string Message);
}
