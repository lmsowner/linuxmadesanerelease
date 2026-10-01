// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;

using LinuxMadeSane.Application.Contracts.HomeLab;

namespace LinuxMadeSane.Web.Services;

public static class TerminalAiAccessPolicy
{
    public static TerminalAiCommandDisposition Evaluate(
        TerminalAiAccessMode accessMode,
        string commandText,
        bool sessionApprovalGranted)
    {
        if (HomeLabTerminalProtocol.TryParse(commandText, out var managed, out _) && managed!.IsReadOnly ||
            TerminalAiCommandGuard.TryValidateForInvestigation(commandText, out _))
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
