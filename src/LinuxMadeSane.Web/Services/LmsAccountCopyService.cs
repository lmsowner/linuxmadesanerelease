// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Collections.Concurrent;
using System.Text.Json;
using LinuxMadeSane.AccountEnrollment;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed record AccountCopyDestination(Guid Id, string Name, string Hostname);

public sealed record AccountCopyDestinations(AccountCopyDestination[] Available, int RegisteredCount, int UnavailableCount);

public sealed class LmsAccountCopyService(IManagedHostStore hosts, ISecurityUserStore users, ISavedCredentialAccessContext access,
    ICommandExecutionService commands, IHostAdministratorCredentials administrators)
{
    private static readonly Lazy<string> Worker = new(() => Convert.ToBase64String(File.ReadAllBytes(typeof(AccountEnrollmentWorker).Assembly.Location)));
    private static readonly string Command = LoadCommand();

    public async Task<AccountCopyDestinations> ListAsync(Guid sourceId, CancellationToken token)
    {
        var user = await GetSourceAsync(sourceId, token);
        var candidates = (await hosts.ListAsync(token)).Where(IsDestination).ToArray();
        var available = new ConcurrentBag<AccountCopyDestination>();
        var unavailable = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token };
        await Parallel.ForEachAsync(candidates, options, async (host, cancellation) =>
        {
            var request = new EnrollmentRequest(Guid.NewGuid(), user.Email, 0, "LMS", null, null, CheckOnly: true);
            try
            {
                var reply = await SendAsync(host, request, cancellation);
                if (reply.Status == "missing") available.Add(new(host.Id, host.Name, host.Hostname));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref unavailable);
            }
        });
        var sorted = available.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Id).ToArray();
        return new(sorted, candidates.Length, unavailable);
    }

    public async Task<EnrollmentReply> EnrollAsync(Guid sourceId, Guid hostId, Guid operationId, string? ticket, string? code, CancellationToken token)
    {
        var user = await GetSourceAsync(sourceId, token);
        var host = await hosts.GetAsync(hostId, token);
        if (host is null || !IsDestination(host)) throw new InvalidOperationException("The destination is no longer a registered remote LMS server.");
        var request = new EnrollmentRequest(operationId, user.Email, user.SessionLifetimeMinutes, $"LMS ({host.Hostname})", ticket, code);
        return await SendAsync(host, request, token);
    }

    private async Task<EnrollmentReply> SendAsync(ManagedHost host, EnrollmentRequest request, CancellationToken token)
    {
        if (commands is not ICommandExecutionInputService transport) throw new InvalidOperationException("SSH credential input is unavailable.");
        var body = JsonSerializer.Serialize(new { Worker = Worker.Value, Request = request }) + "\n";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.CheckOnly ? 20 : 55));
        var remote = host with { DefaultWorkingDirectory = "/" };
        Task<CommandExecutionResult> Send(string cmd, string input) => Task.Run(
            () => transport.ExecuteAsync(remote, cmd, new CommandExecutionInput(input, true), cancellationToken: timeout.Token), timeout.Token);
        CommandExecutionResult result;
        try
        {
            result = await Send(host.Username == "root" ? Command : "sudo -n -- " + Command, body);
            if (host.Username != "root" && !result.IsSuccess && result.StandardError.Contains("sudo:", StringComparison.OrdinalIgnoreCase))
            {
                var credential = await administrators.ResolveAsync(host.Id, host.Username, timeout.Token);
                if (credential is null) throw new UnauthorizedAccessException();
                result = await Send("sudo -S -p '' -- " + Command, credential.Content + body);
                if (!result.IsSuccess && result.StandardError.Contains("sudo:", StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Check the saved SSH administrator credentials for this server."); }
        catch (Exception) { throw new InvalidOperationException("SSH could not complete the request. Check the connection and retry this server."); }
        if (!result.IsSuccess)
            throw new InvalidOperationException("Check that the destination LMS service is running and its account store is accessible, then retry.");
        try
        {
            var reply = JsonSerializer.Deserialize<EnrollmentReply>(result.StandardOutput.Trim());
            if (request.CheckOnly)
            {
                if (reply?.Status is "exists" or "missing") return reply;
            }
            else if (reply?.Status is "verified" or "exists" or "expired" or "invalid") return reply;
            else if (reply is { Status: "pending", Ticket.Length: > 0, OtpUri.Length: > 0 }
                && reply.OtpUri.StartsWith("otpauth://totp/", StringComparison.Ordinal)) return reply;
        }
        catch (JsonException) { }
        throw new InvalidOperationException("The destination returned an unreadable enrollment result. Retry this server.");
    }

    private async Task<SecurityUser> GetSourceAsync(Guid sourceId, CancellationToken token)
    {
        if (await access.GetAuthenticatedUserIdAsync(token) is null)
            throw new UnauthorizedAccessException("Administrator access is required to copy LMS accounts.");
        var user = await users.GetAsync(sourceId, token);
        if (user?.IsEnabled != true) throw new InvalidOperationException("Choose an enabled source account.");
        return user;
    }

    private static bool IsDestination(ManagedHost h) => h.Kind == ManagedHostKind.LmsHost &&
        !ManagedHostCapabilities.Describe(h).IsLocalLmsHost && !AiLocalMachine.IsLoopbackHostname(h.Hostname);

    private static string LoadCommand()
    {
        using var stream = typeof(LmsAccountCopyService).Assembly.GetManifestResourceStream("Lms.AccountEnrollment.Transport")
            ?? throw new InvalidOperationException("The account enrollment transport is missing.");
        using var reader = new StreamReader(stream);
        return "/usr/bin/python3 -I -c " + LogSearchService.Quote(reader.ReadToEnd());
    }
}
