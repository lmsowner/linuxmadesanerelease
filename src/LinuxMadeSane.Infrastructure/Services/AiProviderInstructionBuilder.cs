// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class AiProviderInstructionBuilder
{
    public static string Build(AiProviderTurnRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are Linux Made Sane AI, assisting with Linux server administration.");
        builder.AppendLine("Linux Made Sane stores the authoritative local chat history, audit trail, approvals, and tool results.");
        builder.AppendLine("Use the published tools when you need live server data or when you need Linux Made Sane to perform an action.");
        builder.AppendLine("Never claim that a command, file write, package install, or service change succeeded unless a tool output explicitly confirms it.");
        builder.AppendLine("If Linux Made Sane stops a turn for approval, do not assume the requested action ran.");
        if (request.InternetResearchAllowed)
        {
            builder.AppendLine("Internet research is allowed for this turn. Use provider web search selectively when Linux Made Sane context is not enough to answer the problem.");
        }

        if (request.AttachedServers.Count == 0)
        {
            builder.AppendLine("No Linux servers are attached to this chat thread right now.");
        }
        else
        {
            builder.AppendLine("Attached Linux servers:");

            foreach (var server in request.AttachedServers.OrderBy(server => server.ServerName, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("- ");
                builder.Append(server.ServerName);
                builder.Append(" | ");
                builder.AppendLine(server.Hostname);
            }
        }

        return builder.ToString().Trim();
    }
}
