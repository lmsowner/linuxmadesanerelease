// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class MailRelayClientService(
    IMailRelayStore store,
    ILinuxCommandRunner commandRunner) : IMailRelayClientService
{
    private const string ContainerName = "lms-mail-relay";
    private const string SenderLoginMapPath = "/var/lib/linuxmadesane/mail-relay/config/sender_login_maps";

    public string GeneratePassword() => MailRelayProvisioningService.GeneratePassword();

    public async Task<MailRelayClientSaveResult> SaveAsync(
        MailRelayClientSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        if (configuration?.Enabled != true)
        {
            return Failed("Mail Relay must be running before SMTP users can be changed.");
        }

        var clients = await store.ListClientsAsync(cancellationToken);
        var domains = await store.ListDomainsAsync(cancellationToken);
        var existing = request.ClientId is { } clientId
            ? clients.FirstOrDefault(item => item.Id == clientId)
            : null;
        if (request.ClientId is not null && existing is null)
        {
            return Failed("The SMTP user no longer exists. Refresh Mail Relay and try again.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var username = request.Username?.Trim().ToLowerInvariant() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        if (name.Length is < 1 or > 80)
        {
            return Failed("Enter a name up to 80 characters.");
        }

        if (!MailRelayProvisioningService.IsValidClientUsername(username))
        {
            return Failed("SMTP username must start with a letter or number and contain only letters, numbers, dots, underscores and hyphens.");
        }

        if (existing is not null && !existing.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
        {
            return Failed("SMTP usernames cannot be renamed. Create a new user instead.");
        }

        if (clients.Any(item => item.Id != existing?.Id && item.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            return Failed("That SMTP username already exists.");
        }

        var passwordChanged = !string.IsNullOrEmpty(password);
        if (existing is null && !passwordChanged)
        {
            return Failed("Enter or generate a password for the new SMTP user.");
        }

        if (passwordChanged && (password.Length is < 16 or > 256 || password.IndexOfAny(['\r', '\n', '\0']) >= 0))
        {
            return Failed("SMTP passwords must be between 16 and 256 characters and cannot contain line breaks.");
        }

        var allowedDomains = domains
            .Where(item => item.Enabled && request.AllowedDomainIds.Contains(item.Id))
            .Select(item => item.DomainName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedDomains.Length == 0)
        {
            return Failed("Choose at least one configured sending domain.");
        }

        var now = DateTimeOffset.UtcNow;
        var candidate = existing is null
            ? new MailRelayClient(
                Guid.NewGuid(),
                configuration.Id,
                name,
                username,
                MailRelayProvisioningService.HashCredentialPassword(password),
                true,
                allowedDomains,
                [],
                configuration.DefaultMessagesPerMinute,
                configuration.DefaultMessagesPerDay,
                string.Empty,
                now,
                now,
                null)
            : existing with
            {
                Name = name,
                PasswordHash = passwordChanged
                    ? MailRelayProvisioningService.HashCredentialPassword(password)
                    : existing.PasswordHash,
                AllowedSenderDomains = allowedDomains,
                UpdatedUtc = now
            };

        var allClients = clients.Where(item => item.Id != candidate.Id).Append(candidate).ToArray();
        var senderLoginMap = MailRelayProvisioningService.BuildPostfixSenderLoginMaps(allClients, configuration.RelayHostname);

        try
        {
            await RunRequiredAsync(
                new LinuxCommandRequest(
                    "tee",
                    [SenderLoginMapPath],
                    true,
                    TimeSpan.FromSeconds(20),
                    "Update Mail Relay sender permissions")
                {
                    StandardInputBytes = Encoding.UTF8.GetBytes(senderLoginMap)
                },
                cancellationToken);
            await RunRequiredAsync(
                new LinuxCommandRequest(
                    "chmod",
                    ["0644", SenderLoginMapPath],
                    true,
                    TimeSpan.FromSeconds(20),
                    "Protect Mail Relay sender permissions"),
                cancellationToken);
            await RunRequiredAsync(
                new LinuxCommandRequest(
                    "docker",
                    ["exec", ContainerName, "cp", "/lms-config/sender_login_maps", "/etc/postfix/sender_login_maps"],
                    true,
                    TimeSpan.FromSeconds(20),
                    "Load Mail Relay sender permissions"),
                cancellationToken);
            await RunRequiredAsync(
                new LinuxCommandRequest(
                    "docker",
                    ["exec", ContainerName, "postfix", "reload"],
                    true,
                    TimeSpan.FromSeconds(30),
                    "Reload Mail Relay sender permissions"),
                cancellationToken);

            if (passwordChanged)
            {
                await RunRequiredAsync(
                    new LinuxCommandRequest(
                        "docker",
                        ["exec", "--interactive", ContainerName, "saslpasswd2", "-p", "-c", "-f", "/var/lib/lms/sasldb2", "-u", configuration.RelayHostname, username],
                        true,
                        TimeSpan.FromSeconds(30),
                        "Set the Mail Relay SMTP user password")
                    {
                        StandardInputBytes = Encoding.UTF8.GetBytes(password + "\n")
                    },
                    cancellationToken);
                await RunRequiredAsync(
                    new LinuxCommandRequest(
                        "docker",
                        ["exec", ContainerName, "chown", "root:sasl", "/var/lib/lms/sasldb2"],
                        true,
                        TimeSpan.FromSeconds(20),
                        "Protect the Mail Relay credential database"),
                    cancellationToken);
                await RunRequiredAsync(
                    new LinuxCommandRequest(
                        "docker",
                        ["exec", ContainerName, "chmod", "0640", "/var/lib/lms/sasldb2"],
                        true,
                        TimeSpan.FromSeconds(20),
                        "Restrict the Mail Relay credential database"),
                    cancellationToken);
            }

            await store.SaveClientAsync(candidate, cancellationToken);
            return new MailRelayClientSaveResult(
                true,
                candidate,
                passwordChanged,
                existing is null
                    ? $"SMTP user {username} was added to Postfix."
                    : passwordChanged
                        ? $"SMTP user {username} and its password were updated."
                        : $"SMTP user {username} sender permissions were updated.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed($"SMTP user could not be saved: {FirstUsefulLine(exception.Message)}");
        }
    }

    private async Task RunRequiredAsync(LinuxCommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(request, false, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{request.Description} failed: {FirstUsefulLine(result.StandardError, result.StandardOutput)}");
        }
    }

    private static MailRelayClientSaveResult Failed(string message) => new(false, null, false, message);

    private static string FirstUsefulLine(params string[] values) => values
        .SelectMany(value => (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .FirstOrDefault() ?? "The operation did not return an error message.";

}
