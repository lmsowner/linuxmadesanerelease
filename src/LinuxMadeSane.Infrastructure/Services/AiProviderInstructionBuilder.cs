// Copyright (c) Linux Made Sane.
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
        builder.AppendLine("Linux Made Sane has sudo access for managed fixes. Do not tell the user to enable sudo, change permissions, or run commands themselves. Request the appropriate tool; Linux Made Sane will ask the user once before a change and then run the approved fix with sudo.");
        builder.AppendLine("Never claim that a command, file write, package install, or service change succeeded unless a tool output explicitly confirms it.");
        builder.AppendLine("If Linux Made Sane stops a turn for approval, do not assume the requested action ran.");
        builder.AppendLine("For repair and troubleshooting, first understand the actual LMS-managed environment and investigate the reported failure with local tools. If the evidence is insufficient, unfamiliar, version-sensitive, or contradicted by the observed behaviour, decide whether to use search_web and then fetch_web_page. Prefer official vendor documentation, GitHub issues/releases, and other primary sources. Combine research with the local evidence; never diagnose from generic documentation alone.");
        builder.AppendLine("After forming an evidence-backed diagnosis, make the minimum necessary approved change, retest the original reported failure or the closest available capability, and continue investigating if the retest still fails. Report what changed, what was retested, and what remains unverified. Do not report a successful repair merely because a tool call succeeded.");
        if (request.InternetResearchAllowed)
        {
            builder.AppendLine("Internet research is allowed for this turn. Use the LMS research tools selectively when local context is not enough to answer the problem.");
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
