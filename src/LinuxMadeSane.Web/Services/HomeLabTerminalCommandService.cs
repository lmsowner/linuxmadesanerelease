// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed class HomeLabTerminalCommandService(IHomeLabTerminalOperations operations, IHomeLabService homeLab)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<TerminalAiCommandResult> ExecuteAsync(TerminalTabState tab, string command, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var output = string.Empty; var error = string.Empty; var exitCode = 1;
        try
        {
            if (!tab.IsHomeLabTask || !AiLocalMachine.IsLocalMachine(tab.HostId)) throw new InvalidOperationException("Managed Home Lab operations require the local Home Lab terminal task.");
            if (!HomeLabTerminalProtocol.TryParse(command, out var parsed, out var parseError)) throw new InvalidOperationException(parseError);
            using var args = JsonDocument.Parse(parsed!.ArgumentsJson);
            if (parsed.Operation == "logs")
            {
                var logs = await homeLab.GetLogsAsync(args.RootElement.GetProperty("installationId").GetGuid(), cancellationToken);
                output = JsonSerializer.Serialize(logs, JsonOptions); exitCode = logs.Succeeded ? 0 : 1; error = logs.Error;
            }
            else
            {
                var result = await operations.ExecuteAsync(HomeLabTerminalProtocol.ToolName(parsed.Operation), parsed.ArgumentsJson, cancellationToken);
                exitCode = result.PersistedResult.ExitCode ?? (result.PersistedResult.Outcome == LinuxMadeSane.Core.Enums.AiExecutionOutcome.Succeeded ? 0 : 1);
                error = result.PersistedResult.ErrorText;
                if (result.Response is InspectHomeLabToolResponse inspection)
                {
                    var target = args.RootElement.TryGetProperty("installationId", out var id) ? id.GetGuid() : (Guid?)null;
                    output = JsonSerializer.Serialize(new {
                        result.PersistedResult.Summary, inspection.VpnGateways,
                        Installations = inspection.Installations.Where(item => target is null || item.InstallationId == target).ToArray()
                    }, JsonOptions);
                }
                else output = JsonSerializer.Serialize(result.Response, result.Response.GetType(), JsonOptions);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { error = exception.Message; }
        return new(Guid.NewGuid(), tab.SessionId ?? tab.Id, command, "LMS managed executor", "Home Lab", exitCode,
            output, error, started, DateTimeOffset.UtcNow);
    }
}
