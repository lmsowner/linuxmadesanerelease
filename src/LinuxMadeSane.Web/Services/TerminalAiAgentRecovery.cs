// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Services;

public static class TerminalAiAgentRecovery
{
    private const int MaxErrorMessageLength = 360;

    public static string BuildDispatchFailureFeedback(string command, Exception exception)
    {
        var error = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Trim();
        if (error.Length > MaxErrorMessageLength)
        {
            error = $"{error[..MaxErrorMessageLength]}...";
        }

        return $"Private command channel failure for: {command.Trim()} Error: {error} " +
               "The interactive terminal was not modified. Continue the original request with a focused state check or corrected command. " +
               "Do not ask the operator to retry the chat request.";
    }
}
