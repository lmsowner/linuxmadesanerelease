// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Web.Services;
public sealed class SharedTerminalAiCommandPolicy : ITerminalAiCommandPolicy
{
    public bool IsReadOnly(string command) => TerminalAiCommandGuard.TryValidateForInvestigation(command, out _);
    public bool CanRun(string command, bool sessionApprovalGranted) =>
        TerminalAiAccessPolicy.Evaluate(LinuxMadeSane.Application.Contracts.Ai.TerminalAiAccessMode.Agent,
            command, sessionApprovalGranted) == TerminalAiCommandDisposition.Run;
}
