// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
namespace LinuxMadeSane.Core.Abstractions;
public interface ITerminalAiCommandPolicy
{
    bool IsReadOnly(string command);
    bool CanRun(string command, bool sessionApprovalGranted) => IsReadOnly(command) || sessionApprovalGranted;
}
