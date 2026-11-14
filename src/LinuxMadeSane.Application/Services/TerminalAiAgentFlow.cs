// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
namespace LinuxMadeSane.Application.Services;

public enum TerminalAiAgentStep { CommandCompleted, Retry, Complete, Pause }
public sealed record TerminalAiAgentOutcome(bool Completed, bool CommandLimitReached, bool RecoveryExhausted);
// Shared control flow for the terminal panel and native desktop client. Each surface
// supplies its existing approval, execution and result callbacks, not a second agent.
public static class TerminalAiAgentFlow
{
    public static async Task<TerminalAiAgentOutcome> RunAsync(Func<int, int, CancellationToken, Task<TerminalAiAgentStep>> step,
        int commandLimit = 8, int recoveryLimit = 4, CancellationToken cancellationToken = default)
    {
        var commands = 0; var recoveries = 0;
        while (commands < Math.Clamp(commandLimit, 1, 32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await step(commands, recoveries, cancellationToken))
            {
                case TerminalAiAgentStep.Complete: return new(true, false, false);
                case TerminalAiAgentStep.Pause: return new(false, false, false);
                case TerminalAiAgentStep.Retry:
                    if (++recoveries >= recoveryLimit) return new(false, false, true);
                    break;
                case TerminalAiAgentStep.CommandCompleted: commands++; recoveries = 0; break;
            }
        }
        return new(false, true, false);
    }
}
