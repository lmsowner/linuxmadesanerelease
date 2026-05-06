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
        var userPrompt = BuildPrompt(request, BuildConversationRecap(conversation));
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
                    string.IsNullOrWhiteSpace(request.HostEnvironment) ? "Terminal" : request.HostEnvironment.Trim(),
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

        var assistantEntry = new TerminalAiConversationEntry(
            AiChatMessageRole.Assistant,
            assistantText,
            now,
            ExtractSuggestedCommand(assistantText));

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

    private static string BuildPrompt(TerminalAiPromptRequest request, string conversationRecap)
    {
        var hostLabel = string.IsNullOrWhiteSpace(request.HostName)
            ? "this host"
            : request.HostName.Trim();
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? "Unknown"
            : request.WorkingDirectory.Trim();
        var requestText = TrimRequestText(request.Request);
        var outputTail = TrimTerminalOutput(request.TerminalOutput);

        var builder = new StringBuilder();
        builder.AppendLine("You are helping with an ongoing terminal support conversation.");
        builder.AppendLine("Preserve context from earlier turns and build on what is already known.");
        builder.AppendLine("Do not ask the user to paste large terminal output manually back into chat. If fresh evidence is needed, propose the single next command and assume the interface can run it and analyze the updated terminal session.");
        builder.AppendLine("Do not say 'if you want I can', 'you could try', or similar hedging. State the next fix, check, or command directly.");
        builder.AppendLine("When a command is appropriate, include exactly one fenced bash block so the interface can offer Run then review or Run commands.");
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

    private static string TrimTerminalOutput(string terminalOutput)
    {
        if (string.IsNullOrWhiteSpace(terminalOutput))
        {
            return "(No terminal output captured yet)";
        }

        var normalized = terminalOutput.Trim();
        if (normalized.Length <= MaxTerminalOutputChars)
        {
            return normalized;
        }

        return $"[recent output tail]{Environment.NewLine}{normalized[^MaxTerminalOutputChars..]}";
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
        var match = SuggestedCommandPattern().Match(assistantText);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups[1].Value.Trim();
    }

    [GeneratedRegex("```(?:bash|sh)?\\s*\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuggestedCommandPattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespacePattern();
}
