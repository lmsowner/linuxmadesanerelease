// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class OllamaRuntimeService(
    ILinuxCommandRunner commandRunner,
    ILocalAiEngineStore store,
    ILocalModelManagementService modelManagementService) : IOllamaRuntimeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DefaultEndpoint = "http://127.0.0.1:11434";
    private const string DefaultKeepAlive = "1h";

    public async Task<LocalAiRuntime> InspectAsync(CancellationToken cancellationToken = default)
    {
        var whichTask = RunOptionalAsync(
            new LinuxCommandRequest("which", ["ollama"], false, TimeSpan.FromSeconds(10), "Detect Ollama binary")
            {
                IsOptionalExternalTool = true
            },
            cancellationToken);
        var versionTask = RunOptionalAsync(
            new LinuxCommandRequest("ollama", ["--version"], false, TimeSpan.FromSeconds(10), "Detect Ollama version")
            {
                IsOptionalExternalTool = true
            },
            cancellationToken);
        var isActiveTask = RunOptionalAsync(
            new LinuxCommandRequest("systemctl", ["is-active", "ollama"], false, TimeSpan.FromSeconds(10), "Inspect Ollama service")
            {
                IsOptionalExternalTool = true
            },
            cancellationToken);
        var isEnabledTask = RunOptionalAsync(
            new LinuxCommandRequest("systemctl", ["is-enabled", "ollama"], false, TimeSpan.FromSeconds(10), "Inspect Ollama service enablement")
            {
                IsOptionalExternalTool = true
            },
            cancellationToken);

        await Task.WhenAll(whichTask, versionTask, isActiveTask, isEnabledTask);

        var endpoint = (await store.GetSettingsAsync(cancellationToken)).RuntimeEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = DefaultEndpoint;
        }

        var isApiReachable = await ProbeApiAsync(endpoint, cancellationToken);
        var executablePath = whichTask.Result.ExitCode == 0 ? whichTask.Result.StandardOutput.Trim() : string.Empty;
        var version = versionTask.Result.ExitCode == 0
            ? versionTask.Result.StandardOutput.Trim()
            : string.Empty;
        var isInstalled = !string.IsNullOrWhiteSpace(executablePath);
        var isServiceActive = isActiveTask.Result.ExitCode == 0 &&
                              isActiveTask.Result.StandardOutput.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        var isServiceEnabled = isEnabledTask.Result.ExitCode == 0 &&
                               isEnabledTask.Result.StandardOutput.Trim().Contains("enabled", StringComparison.OrdinalIgnoreCase);

        var detail = !isInstalled
            ? "Ollama is not installed on this LMS host."
            : !isServiceActive
                ? "Ollama is installed but the systemd service is not active."
                : !isApiReachable
                    ? "Ollama is installed and the service is active, but the localhost API did not respond."
                    : "Ollama is installed, running, and reachable on localhost.";

        return new LocalAiRuntime(
            LocalAiRuntimeKind.Ollama,
            "Ollama",
            executablePath,
            version,
            "ollama",
            endpoint,
            isInstalled,
            isServiceActive,
            isServiceEnabled,
            isApiReachable,
            !isInstalled
                ? LocalAiHealthState.Warning
                : isServiceActive && isApiReachable
                    ? LocalAiHealthState.Healthy
                    : LocalAiHealthState.Warning,
            detail,
            DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<LocalAiInstalledModel>> ListInstalledModelsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        var endpoint = string.IsNullOrWhiteSpace(settings.RuntimeEndpoint) ? DefaultEndpoint : settings.RuntimeEndpoint;
        var models = await TryListViaApiAsync(endpoint, settings.DefaultModelId, cancellationToken);
        if (models.Count > 0)
        {
            return models;
        }

        var command = await commandRunner.RunAsync(
            new LinuxCommandRequest("ollama", ["list"], false, TimeSpan.FromMinutes(1), "List local Ollama models")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (command.ExitCode != 0)
        {
            return [];
        }

        return ParseModelTable(command.StandardOutput, settings.DefaultModelId);
    }

    public async Task<LocalAiApplyResult> InstallAsync(bool approved, CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            return new LocalAiApplyResult(
                false,
                "Ollama install blocked.",
                "Linux Made Sane requires explicit approval before installing the local AI runtime.",
                false,
                [],
                DateTimeOffset.UtcNow);
        }

        var output = new List<string>();
        var download = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", "curl -fsSL https://ollama.com/install.sh -o /tmp/lms-ollama-install.sh"],
                true,
                TimeSpan.FromMinutes(2),
                "Download Ollama install script"),
            dryRun: false,
            cancellationToken);
        AppendOutput(output, download);
        if (download.ExitCode != 0)
        {
            return Failed("Ollama install failed.", "The install script could not be downloaded.", output);
        }

        var install = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "sh",
                ["/tmp/lms-ollama-install.sh"],
                true,
                TimeSpan.FromMinutes(10),
                "Run Ollama install script"),
            dryRun: false,
            cancellationToken);
        AppendOutput(output, install);
        if (install.ExitCode != 0)
        {
            return Failed("Ollama install failed.", "The Ollama install script reported an error.", output);
        }

        var enable = await commandRunner.RunAsync(
            new LinuxCommandRequest("systemctl", ["enable", "--now", "ollama"], true, TimeSpan.FromMinutes(1), "Enable Ollama service"),
            dryRun: false,
            cancellationToken);
        AppendOutput(output, enable);
        if (enable.ExitCode != 0)
        {
            return Failed("Ollama install completed with service errors.", "Ollama installed, but Linux Made Sane could not enable and start the service.", output);
        }

        return new LocalAiApplyResult(
            true,
            "Ollama installed.",
            "The local AI runtime is installed and the systemd service has been enabled.",
            false,
            output,
            DateTimeOffset.UtcNow);
    }

    public Task<LocalAiApplyResult> StartAsync(bool approved, CancellationToken cancellationToken = default) =>
        ControlServiceAsync("start", "Start Ollama service", "Ollama started.", "The local Ollama service is now active.", approved, cancellationToken);

    public Task<LocalAiApplyResult> StopAsync(bool approved, CancellationToken cancellationToken = default) =>
        ControlServiceAsync("stop", "Stop Ollama service", "Ollama stopped.", "The local Ollama service has been stopped.", approved, cancellationToken);

    public Task<LocalAiApplyResult> RestartAsync(bool approved, CancellationToken cancellationToken = default) =>
        ControlServiceAsync("restart", "Restart Ollama service", "Ollama restarted.", "The local Ollama service was restarted.", approved, cancellationToken);

    public async Task<LocalAiApplyResult> PullModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            return Failed("Model pull blocked.", "Linux Made Sane requires explicit approval before pulling a local model.", []);
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest("ollama", ["pull", modelId.Trim()], false, TimeSpan.FromMinutes(30), $"Pull Ollama model {modelId}"),
            dryRun: false,
            cancellationToken);

        var output = new List<string>();
        AppendOutput(output, result);
        return result.ExitCode == 0
            ? new LocalAiApplyResult(true, "Model pulled.", $"{modelId} is now available locally.", false, output, DateTimeOffset.UtcNow)
            : Failed("Model pull failed.", $"Ollama could not pull {modelId}.", output);
    }

    public async Task<LocalAiApplyResult> RemoveModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default)
    {
        if (!approved)
        {
            return Failed("Model removal blocked.", "Linux Made Sane requires explicit approval before removing a local model.", []);
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest("ollama", ["rm", modelId.Trim()], false, TimeSpan.FromMinutes(5), $"Remove Ollama model {modelId}"),
            dryRun: false,
            cancellationToken);

        var output = new List<string>();
        AppendOutput(output, result);
        return result.ExitCode == 0
            ? new LocalAiApplyResult(true, "Model removed.", $"{modelId} has been removed from the local Ollama cache.", false, output, DateTimeOffset.UtcNow)
            : Failed("Model removal failed.", $"Ollama could not remove {modelId}.", output);
    }

    public async Task<LocalAiBenchmarkResult> TestModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var request = new AiProviderTurnRequest(
                new AiChatThread(
                    Guid.NewGuid(),
                    "Local AI test",
                    "local-ollama",
                    AiProviderType.Ollama,
                    modelId,
                    AiTrustProfile.CreatePreset(AiTrustLevel.Guided),
                    string.Empty,
                    string.Empty,
                    startedAtUtc,
                    startedAtUtc),
                [],
                [],
                [new AiProviderMessageInputItem(AiChatMessageRole.User, "Reply with exactly OK.")],
                [],
                false,
                false);

            var result = await ExecuteAsync(modelId, request, cancellationToken: cancellationToken);
            stopwatch.Stop();

            var text = string.Join(" ", result.AssistantOutputs.Select(item => item.Content)).Trim();
            return new LocalAiBenchmarkResult(
                Guid.NewGuid(),
                modelId,
                "Reply with exactly OK.",
                text.Contains("OK", StringComparison.OrdinalIgnoreCase),
                stopwatch.Elapsed,
                string.IsNullOrWhiteSpace(text) ? "The model returned no assistant text." : text,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new LocalAiBenchmarkResult(
                Guid.NewGuid(),
                modelId,
                "Reply with exactly OK.",
                false,
                stopwatch.Elapsed,
                exception.Message,
                DateTimeOffset.UtcNow);
        }
    }

    public async Task<AiProviderTurnResult> ExecuteAsync(
        string modelId,
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("An Ollama model must be selected before Linux Made Sane can execute a local AI turn.");
        }

        var settings = await store.GetSettingsAsync(cancellationToken);
        var endpoint = string.IsNullOrWhiteSpace(settings.RuntimeEndpoint) ? DefaultEndpoint : settings.RuntimeEndpoint.Trim().TrimEnd('/');
        using var httpClient = CreateLocalApiClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/api/chat")
        {
            Content = new StringContent(BuildRequestBody(modelId, request), Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(responseBody)
                ? $"Ollama returned HTTP {(int)response.StatusCode}."
                : responseBody);
        }

        var payload = JsonNode.Parse(responseBody)?.AsObject()
                      ?? throw new InvalidOperationException("Ollama returned an unreadable response.");
        var message = payload["message"]?.AsObject();
        var content = message?["content"]?.GetValue<string>() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(content) && textProgress is not null)
        {
            textProgress.Report(new AiProviderTextDelta("ollama", content));
        }

        var toolCalls = ParseToolCalls(message?["tool_calls"]);
        return new AiProviderTurnResult(
            payload["created_at"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            null,
            payload["model"]?.GetValue<string>() ?? modelId,
            string.IsNullOrWhiteSpace(content) ? [] : [new AiProviderAssistantOutput(content)],
            toolCalls);
    }

    private static string BuildRequestBody(string modelId, AiProviderTurnRequest request)
    {
        var messages = new JsonArray();
        var systemInstruction = AiProviderInstructionBuilder.Build(request);
        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemInstruction
            });
        }

        foreach (var inputItem in request.InputItems)
        {
            switch (inputItem)
            {
                case AiProviderMessageInputItem message:
                    messages.Add(new JsonObject
                    {
                        ["role"] = message.Role switch
                        {
                            AiChatMessageRole.Assistant => "assistant",
                            AiChatMessageRole.System => "system",
                            AiChatMessageRole.Tool => "tool",
                            _ => "user"
                        },
                        ["content"] = message.Role == AiChatMessageRole.Tool
                            ? $"Linux Made Sane tool output:\n{message.Content}"
                            : message.Content
                    });
                    break;

                case AiProviderToolCallInputItem toolCall:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = $"Linux Made Sane previously requested tool {toolCall.ToolName} with arguments {toolCall.ArgumentsJson}."
                    });
                    break;

                case AiProviderToolOutputInputItem toolOutput:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["content"] = toolOutput.OutputJson
                    });
                    break;

                default:
                    throw new InvalidOperationException($"The provider input type {inputItem.GetType().Name} is not supported by the Ollama adapter.");
            }
        }

        var payload = new JsonObject
        {
            ["model"] = modelId,
            ["stream"] = false,
            ["messages"] = messages,
            ["keep_alive"] = DefaultKeepAlive
        };

        if (request.AvailableTools.Count > 0)
        {
            payload["tools"] = new JsonArray(
                request.AvailableTools.Select(tool => new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = AiToolJsonSchemaCatalog.ParseParametersSchema(tool)
                    }
                }).ToArray<JsonNode?>());
        }

        return payload.ToJsonString(JsonOptions);
    }

    private static IReadOnlyList<AiProviderToolCallRequest> ParseToolCalls(JsonNode? node)
    {
        if (node is not JsonArray items)
        {
            return [];
        }

        var toolCalls = new List<AiProviderToolCallRequest>();
        foreach (var item in items)
        {
            var function = item?["function"]?.AsObject();
            var name = function?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var arguments = function?["arguments"];
            var argumentsJson = arguments is null
                ? "{}"
                : arguments is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var asString)
                    ? NormalizeArgumentString(asString)
                    : arguments.ToJsonString(JsonOptions);

            toolCalls.Add(new AiProviderToolCallRequest(
                item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                name.Trim(),
                argumentsJson));
        }

        return toolCalls;
    }

    private async Task<IReadOnlyList<LocalAiInstalledModel>> TryListViaApiAsync(
        string endpoint,
        string defaultModelId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = CreateLocalApiClient(TimeSpan.FromSeconds(10));
            var payload = await httpClient.GetFromJsonAsync<JsonObject>($"{endpoint.TrimEnd('/')}/api/tags", cancellationToken);
            var models = payload?["models"] as JsonArray;
            if (models is null)
            {
                return [];
            }

            return models
                .Select(item => item?.AsObject())
                .Where(item => item is not null)
                .Select(item =>
                {
                    var modelId = item!["name"]?.GetValue<string>() ?? string.Empty;
                    var definition = modelManagementService.FindDefinition(modelId);
                    return new LocalAiInstalledModel(
                        modelId,
                        definition?.DisplayName ?? modelId,
                        item["size"]?.GetValue<long?>() ?? 0,
                        item["digest"]?.GetValue<string>() ?? string.Empty,
                        ParseDateTimeOffset(item["modified_at"]?.GetValue<string>()),
                        false,
                        modelId.Equals(defaultModelId, StringComparison.OrdinalIgnoreCase),
                        definition?.Capabilities ?? (AiProviderCapabilityFlag.BasicChat | AiProviderCapabilityFlag.CommandExplanation),
                        definition?.Description ?? "Installed local model.");
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.ModelId))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<LocalAiInstalledModel> ParseModelTable(string stdout, string defaultModelId) =>
        SplitLines(stdout)
            .Skip(1)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 3)
            .Select(parts =>
            {
                var modelId = parts[0];
                return new LocalAiInstalledModel(
                    modelId,
                    modelId,
                    ParseHumanSize(parts[2]),
                    string.Empty,
                    ParseDateTimeOffset(parts.Last()),
                    false,
                    modelId.Equals(defaultModelId, StringComparison.OrdinalIgnoreCase),
                    AiProviderCapabilityFlag.BasicChat | AiProviderCapabilityFlag.CommandExplanation,
                    "Installed local model.");
            })
            .ToArray();

    private static IReadOnlyList<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeArgumentString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "{}";
        }

        try
        {
            JsonNode.Parse(value);
            return value;
        }
        catch
        {
            return JsonSerializer.Serialize(new { value });
        }
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static long ParseHumanSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var trimmed = value.Trim();
        var suffix = trimmed[^1];
        if (char.IsDigit(suffix) && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            return raw;
        }

        if (!double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return 0;
        }

        var multiplier = char.ToUpperInvariant(suffix) switch
        {
            'K' => 1024d,
            'M' => 1024d * 1024d,
            'G' => 1024d * 1024d * 1024d,
            'T' => 1024d * 1024d * 1024d * 1024d,
            _ => 1d
        };

        return (long)(amount * multiplier);
    }

    private async Task<bool> ProbeApiAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = CreateLocalApiClient(TimeSpan.FromSeconds(5));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await httpClient.GetAsync($"{endpoint.TrimEnd('/')}/api/tags", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private Task<LinuxCommandResult> RunOptionalAsync(LinuxCommandRequest request, CancellationToken cancellationToken) =>
        commandRunner.RunAsync(request, dryRun: false, cancellationToken);

    // Use a direct local client for Ollama so "not listening on localhost" stays an expected health state
    // instead of being emitted through the shared HttpClientFactory logging pipeline.
    private static HttpClient CreateLocalApiClient(TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler())
        {
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };

    private async Task<LocalAiApplyResult> ControlServiceAsync(
        string action,
        string requestDescription,
        string successSummary,
        string successDetail,
        bool approved,
        CancellationToken cancellationToken)
    {
        if (!approved)
        {
            return Failed(
                $"{successSummary.TrimEnd('.')} blocked.",
                $"Linux Made Sane requires explicit approval before it will {action} the Ollama service.",
                []);
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest("systemctl", [action, "ollama"], true, TimeSpan.FromMinutes(1), requestDescription),
            dryRun: false,
            cancellationToken);

        var output = new List<string>();
        AppendOutput(output, result);
        return result.ExitCode == 0
            ? new LocalAiApplyResult(true, successSummary, successDetail, false, output, DateTimeOffset.UtcNow)
            : Failed($"{successSummary.TrimEnd('.')} failed.", $"Linux Made Sane could not {action} the Ollama service.", output);
    }

    private static void AppendOutput(List<string> output, LinuxCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.CommandText))
        {
            output.Add($"> {result.CommandText}");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            output.AddRange(SplitLines(result.StandardOutput));
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output.AddRange(SplitLines(result.StandardError));
        }
    }

    private static LocalAiApplyResult Failed(string summary, string detail, IReadOnlyList<string> output) =>
        new(false, summary, detail, false, output, DateTimeOffset.UtcNow);
}
