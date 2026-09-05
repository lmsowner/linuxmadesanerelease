// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Web.Services;

public static class TerminalAiAccessPolicy
{
    public static TerminalAiCommandDisposition Evaluate(
        TerminalAiAccessMode accessMode,
        string commandText,
        bool sessionApprovalGranted)
    {
        if (TerminalAiCommandGuard.TryValidateForInvestigation(commandText, out _))
        {
            return TerminalAiCommandDisposition.Run;
        }

        return accessMode switch
        {
            TerminalAiAccessMode.ReadOnly => TerminalAiCommandDisposition.Block,
            TerminalAiAccessMode.FullAccess => TerminalAiCommandDisposition.Run,
            TerminalAiAccessMode.Agent when sessionApprovalGranted => TerminalAiCommandDisposition.Run,
            _ => TerminalAiCommandDisposition.RequireApproval
        };
    }
}

public enum TerminalAiCommandDisposition
{
    Run = 0,
    RequireApproval = 1,
    Block = 2
}
