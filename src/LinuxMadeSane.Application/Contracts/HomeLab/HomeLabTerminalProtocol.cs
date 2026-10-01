// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.Json;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public sealed record HomeLabTerminalCommand(string Operation, string ArgumentsJson, bool IsReadOnly);

public static class HomeLabTerminalProtocol
{
    public const string Prefix = "lms-home-lab";
    public const string Instructions = """
        This is a Home Lab task in the regular terminal agent. Managed commands run through LMS itself, without SSH or sudo. Put exactly one command in a bash block, as usual:
        lms-home-lab inspect
        lms-home-lab inspect '{"installationId":"UUID"}'
        lms-home-lab deploy '{"promptRecipeId":"recipe-id","listenAddress":"127.0.0.1","outboundRoute":"direct","storagePaths":{"movies":"/mnt/storage/movies","tv":"/mnt/storage/tv","home-lab":"/mnt/storage/home-lab"}}'
        lms-home-lab repair '{"installationId":"UUID"}'
        lms-home-lab logs '{"installationId":"UUID"}'
        lms-home-lab config '{"installationId":"UUID","relativePath":"optional-file"}'
        lms-home-lab patch '{"installationId":"UUID","relativePath":"file","expectedSha256":"current-sha256","expectedText":"exact-old-text","replacementText":"exact-new-text"}'
        For VPN deployments, set outboundRoute to vpn and vpnGatewayInstallationId to the selected UUID. Only the recipe's declared VPN apps use that gateway.
        Prefer managed deploy/repair for LMS-owned containers so saved configuration stays consistent. These commands are handled by LMS, not a shell: never prefix them with sudo, SSH, curl or a pipeline. Use only the chosen storage paths; ask if a different path is needed. Planning recipes must be planned rather than passed to deploy. Explain unsupported custom requirements before changing anything.
        Inspect real evidence first. If managed repair does not fix the reported symptom, inspect logs and redacted config, then make the minimum supported patch or use focused shell diagnostics through the connected terminal. Do not repeatedly run the same ineffective repair or claim success from a container health check alone. Verify the actual requested endpoint or capability. Preserve user data and existing working services. Handed-off containers belong to their Docker manager; do not change them through LMS.
        Ordinary shell commands still require an active SSH terminal and use the connected account's permissions. Connect that terminal if deeper shell diagnostics are needed. Never request passwords, keys or VPN secrets in chat. Report a genuine blocker clearly.
        """;

    public static bool IsManagedCommand(string command) => command.TrimStart().StartsWith(Prefix, StringComparison.Ordinal) &&
        (command.TrimStart().Length == Prefix.Length || !char.IsLetterOrDigit(command.TrimStart()[Prefix.Length]));

    public static bool TryParse(string command, out HomeLabTerminalCommand? parsed, out string error)
    {
        parsed = null; error = "Use one lms-home-lab operation with a JSON object; shell operators are not supported.";
        if (!IsManagedCommand(command) || command.Length > 20000) return false;
        var remainder = command.Trim()[Prefix.Length..].TrimStart();
        var separator = remainder.IndexOf(' ');
        var operation = separator < 0 ? remainder : remainder[..separator];
        if (operation is not ("inspect" or "deploy" or "repair" or "logs" or "config" or "patch")) return false;
        var arguments = separator < 0 ? "{}" : remainder[(separator + 1)..].Trim();
        if (arguments.Length >= 2 && arguments[0] == '\'' && arguments[^1] == '\'') arguments = arguments[1..^1];
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            parsed = new(operation, document.RootElement.GetRawText(), operation is "inspect" or "logs" or "config");
            error = string.Empty;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string ToolName(string operation) => operation switch
    {
        "inspect" => AiToolNames.InspectHomeLab,
        "deploy" => AiToolNames.ApplyHomeLabPromptRecipe,
        "repair" => AiToolNames.RepairHomeLabInstallation,
        "config" => AiToolNames.InspectHomeLabApplicationConfig,
        "patch" => AiToolNames.RepairHomeLabApplicationConfig,
        _ => throw new InvalidOperationException("This operation has no managed tool handler.")
    };
}
