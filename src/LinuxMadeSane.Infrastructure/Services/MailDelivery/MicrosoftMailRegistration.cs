// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text;
using System.Net.Mail;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record MicrosoftMailRegistrationResult(string TenantId, string ClientId)
{
    public string ServicePrincipalId { get; init; } = "";
    public string SenderAddress { get; init; } = "";
    public string MailboxObjectId { get; init; } = "";
    public MicrosoftMailSetupStage Stage { get; init; } = MicrosoftMailSetupStage.MailboxAuthorised;
    public string Issue { get; init; } = "";
    public string Diagnostic { get; init; } = "";
}
public sealed record MicrosoftMailRegistrationProgress(string Message, string? DeviceCode = null) { public string Stage { get; init; } = ""; }

// Run the existing provisioning script as the LMS service account. Only recognised
// progress, sign-in instructions and redacted Exchange responses reach the UI.
// Raw SDK output is never logged.
public sealed class MicrosoftMailRegistration(IStreamingLinuxCommandRunner runner)
{
    public Task<MicrosoftMailRegistrationResult> RegisterAsync(string script,
        Action<MicrosoftMailRegistrationProgress> progress, CancellationToken token = default)
        => RegisterAsync(script, progress, static (_, _) => Task.CompletedTask, token);

    public async Task<MicrosoftMailRegistrationResult> RegisterAsync(string script,
        Action<MicrosoftMailRegistrationProgress> progress,
        Func<MicrosoftMailRegistrationResult, CancellationToken, Task> persistIdentity,
        CancellationToken token = default)
    {
        var executable = await MailProviderPrerequisites.EnsurePowerShellAsync(runner, progress, token);
        MicrosoftMailRegistrationResult? identity = null, checkpoint = null;
        string? failure = null;
        var outputLock = new object(); var signInText = ""; var stage = "modules";
        var checkpoints = Channel.CreateUnbounded<MicrosoftMailRegistrationResult>(new UnboundedChannelOptions { SingleReader = true });
        async Task PersistAsync()
        {
            await foreach (var checkpoint in checkpoints.Reader.ReadAllAsync())
                await persistIdentity(checkpoint, CancellationToken.None);
        }
        void PreserveIncomplete()
        {
            lock (outputLock)
                if (checkpoint is not null && checkpoint.Stage != MicrosoftMailSetupStage.MailboxAuthorised)
                    checkpoints.Writer.TryWrite(checkpoint with { Issue = checkpoint.Stage == MicrosoftMailSetupStage.ExchangeAuthenticated
                        ? "Exchange sign-in succeeded, but mailbox setup stopped before permissions were verified. Your application details are saved."
                        : "Your application registration is saved. Exchange mailbox setup did not finish." });
        }
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var persistence = PersistAsync();
        LinuxCommandResult result;
        Task<LinuxCommandResult>? execution = null;
        try
        {
            execution = runner.RunStreamingAsync(new(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                false, TimeSpan.FromMinutes(20), "Register certificate-based Microsoft mail delivery")
                { SensitiveArgumentIndexes = new HashSet<int> { 6 }, IsOptionalExternalTool = true }, false, output =>
            {
                lock (outputLock)
                {
                    var line = Regex.Replace(output.Text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
                    const string prefix = "LMS_MAIL_SETUP:";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        try
                        {
                            using var json = JsonDocument.Parse(line[prefix.Length..]); var item = json.RootElement;
                            if ((item.GetProperty("kind").GetString() is "registered" or "exchange-connected" or "mailbox-resolved" or "complete") &&
                                (item.GetProperty("kind").GetString() != "complete" || failure is null) &&
                                Guid.TryParse(item.GetProperty("tenantId").GetString(), out var tenant) && tenant != Guid.Empty &&
                                Guid.TryParse(item.GetProperty("clientId").GetString(), out var client) && client != Guid.Empty)
                            {
                                string Identifier(string name) => item.TryGetProperty(name, out var value) && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty ? id.ToString() : "";
                                var resolvedSender = "";
                                if (item.TryGetProperty("senderAddress", out var address))
                                {
                                    if (!MailAddress.TryCreate(address.GetString(), out var parsed) || parsed.Address != address.GetString() || string.IsNullOrEmpty(Identifier("mailboxId"))) return;
                                    resolvedSender = parsed.Address;
                                }
                                var captured = new MicrosoftMailRegistrationResult(tenant.ToString(), client.ToString())
                                    { ServicePrincipalId = Identifier("principalId"), SenderAddress = resolvedSender, MailboxObjectId = Identifier("mailboxId"), Stage = item.GetProperty("kind").GetString() switch {
                                        "complete" => MicrosoftMailSetupStage.MailboxAuthorised,
                                        "exchange-connected" or "mailbox-resolved" => MicrosoftMailSetupStage.ExchangeAuthenticated,
                                        _ => MicrosoftMailSetupStage.ApplicationRegistered } };
                                checkpoint = captured;
                                checkpoints.Writer.TryWrite(captured);
                                if (item.GetProperty("kind").GetString() == "complete") identity = captured;
                            }
                            else if (item.GetProperty("kind").GetString() == "error")
                            {
                                failure ??= item.GetProperty("stage").GetString() switch
                                {
                                    "tenant" => "Graph and Exchange were signed in to different tenants. Retry and use accounts in the same organisation for both sign-ins.",
                                    "sender" => "Use the mailbox's primary email address as the sender, then retry registration.",
                                    "modules" => "Microsoft setup tools could not be loaded. Retry here; LMS prepares its dependencies automatically.",
                                    "graph" => "Entra sign-in did not finish. Retry registration with your Microsoft 365 administrator account.",
                                    "application" => "Entra could not finish registering the application. Check the administrator can create applications and service principals, then retry. Saved registration details are retained.",
                                    "exchange" => "LMS did not establish a usable Exchange session after requesting Microsoft approval. Your Entra application is saved; mailbox setup is unfinished.",
                                    "exchange-preflight" => "Exchange sign-in succeeded, but this session does not provide the commands required to configure mailbox access. Use an Exchange administrator with Role Management permissions. See the Exchange response below for the missing command.",
                                    "principal-read" => "Exchange could not read its application registrations. See the Exchange response below.",
                                    "scope-conflict" => "The existing named Exchange scope has a different or broader mailbox restriction. LMS has left it unchanged. Review that scope before continuing.",
                                    "scope-read" => "Exchange could not read mailbox sending scopes. See the Exchange response below.",
                                    "role-read" => "Exchange could not read mailbox sending role assignments. See the Exchange response below.",
                                    "mailbox" => "Exchange sign-in succeeded, but the sender mailbox could not be read. Check that it exists in this tenant and the administrator can access it. Your application details are saved.",
                                    "principal" => "Exchange sign-in succeeded, but the application could not be registered in Exchange. Check Exchange administrator permissions. Your Entra application is saved.",
                                    "scope-create" => "Exchange could not create the mailbox sending scope. See the Exchange response below for the reason. Your application details are saved.",
                                    "role" => "Exchange sign-in succeeded, but the mailbox's Application Mail.Send role could not be assigned. Check Exchange role-management permissions. Your application details are saved.",
                                    "verify" => "Exchange sign-in succeeded, but mailbox permission verification did not finish. Your application details are saved.",
                                    "scope" => "Microsoft has not confirmed the mailbox sending scope yet. Allow permission propagation, then retry registration to resume.",
                                    _ => "Microsoft registration did not finish. Retry to resume; live mail was not changed."
                                };
                                if (checkpoint is not null)
                                {
                                    var diagnostic = "";
                                    if (checkpoint.Stage == MicrosoftMailSetupStage.ExchangeAuthenticated && item.TryGetProperty("detail", out var detail))
                                    {
                                        var command = item.TryGetProperty("command", out var commandValue) ? commandValue.GetString() : null;
                                        var allowed = new[] { "Get-EXOMailbox", "Get-ServicePrincipal", "New-ServicePrincipal", "Get-ManagementScope", "New-ManagementScope", "Get-ManagementRoleAssignment", "New-ManagementRoleAssignment", "Test-ServicePrincipalAuthorization" };
                                        if (allowed.Contains(command)) diagnostic = command + ": " + RedactExchangeResponse(detail.GetString() ?? "");
                                    }
                                    checkpoints.Writer.TryWrite(checkpoint with { Issue = failure, Diagnostic = diagnostic });
                                }
                            }
                            else if (item.GetProperty("kind").GetString() == "progress")
                            {
                                signInText = "";
                                // Map fixed stages; never forward an arbitrary script/SDK message.
                                stage = item.GetProperty("stage").GetString() ?? "";
                                var message = stage switch
                                {
                                    "modules" => "Preparing Microsoft PowerShell modules for the LMS service account…",
                                    "graph" => "Sign in to Microsoft to register the sending application.",
                                    "application" => "Registering the public certificate in your signed-in tenant…",
                                    "exchange" => "Sign in to Exchange Online to authorise the sender mailbox.",
                                    "exchange-preflight" => "Checking the signed-in Exchange account's setup capabilities…",
                                    "principal-read" => "Checking the application's Exchange registration…",
                                    "scope-read" => "Checking for the mailbox sending scope…",
                                    "scope-reuse" => "Reusing an existing restriction for this mailbox…",
                                    "role-read" => "Checking for the mailbox sending role assignment…",
                                    "mailbox" => "Exchange sign-in succeeded. Checking the sender mailbox…",
                                    "principal" => "Registering the application in Exchange…",
                                    "scope-create" => "Configuring the mailbox sending scope…",
                                    "role" => "Assigning permission to send from this mailbox…",
                                    "verify" => "Verifying mailbox sending permissions…",
                                    _ => null
                                };
                                if (message is not null) progress(new(message) { Stage = stage });
                            }
                        }
                        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { }
                        return;
                    }
                    signInText += " " + line;
                    if (signInText.Length > 4096) signInText = signInText[^4096..];
                    var compact = Regex.Replace(signInText, @"\s+", "");
                    var code = Regex.Match(signInText, @"(?:enter|use)\s+(?:the\s+)?code\s+([A-Z0-9-]{6,32})\b", RegexOptions.IgnoreCase);
                    if (code.Success && (compact.Contains("https://microsoft.com/devicelogin", StringComparison.OrdinalIgnoreCase) ||
                        compact.Contains("https://login.microsoft.com/device", StringComparison.OrdinalIgnoreCase)))
                    {
                        progress(new("Open Microsoft sign-in and enter this code.", code.Groups[1].Value.ToUpperInvariant()) { Stage = stage });
                        signInText = "";
                    }
                }
            }, executionCancellation.Token);
            if (await Task.WhenAny(execution, persistence) == persistence && persistence.IsFaulted)
            {
                executionCancellation.Cancel();
                try { await execution; } catch (OperationCanceledException) { }
            }
            result = await execution;
            if ((result.ExitCode != 0 || identity is null) && failure is null) PreserveIncomplete();
        }
        catch (OperationCanceledException) { PreserveIncomplete(); throw; }
        finally
        {
            checkpoints.Writer.TryComplete();
            try { await persistence; }
            catch { throw new InvalidOperationException("Microsoft application details could not be saved. Registration has stopped; retry here after checking LMS storage. Live mail has not changed."); }
        }
        token.ThrowIfCancellationRequested();
        if (result.ExitCode is 127 or 78) throw new InvalidOperationException("The Microsoft sign-in runtime became unavailable. Retry here; LMS will prepare its tools again.");
        if (result.ExitCode == 124) throw new InvalidOperationException(stage is "exchange" or "mailbox"
            ? "Exchange authorisation timed out. Your Entra registration is saved; continue mailbox authorisation to resume."
            : "Microsoft registration timed out. Retry here; saved registration details are retained.");
        if (failure is not null) throw new InvalidOperationException(failure);
        if (result.ExitCode != 0 || identity is null) throw new InvalidOperationException("Microsoft registration did not finish. Check the administrator can manage Entra applications and Exchange application roles, and that device sign-in and PowerShell Gallery access are allowed. Retry to resume; live mail was not changed.");
        return identity;
    }
    internal static string RedactExchangeResponse(string value)
    {
        value = Regex.Replace(value, @"-----BEGIN[^-]*-----[\s\S]*?(?:-----END[^-]*-----|$)", "[redacted key]");
        value = Regex.Replace(value, @"(?i)\b(?:Bearer|Basic)\s+[^\s""']+", "[redacted authorization]");
        value = Regex.Replace(value, @"(?i)(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|password|authorization)\s*[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)", "[redacted credential]");
        value = Regex.Replace(value, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[redacted token]");
        value = Regex.Replace(value, @"https?://[^\s<>""']+", match => Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path) : "[redacted URL]");
        value = Regex.Replace(value, @"[A-Za-z0-9_+/=-]{96,}", "[redacted value]");
        value = Regex.Replace(value, @"[\x00-\x1f\x7f]+", " ").Trim();
        return value.Length > 600 ? value[..600] + "…" : value;
    }

}
