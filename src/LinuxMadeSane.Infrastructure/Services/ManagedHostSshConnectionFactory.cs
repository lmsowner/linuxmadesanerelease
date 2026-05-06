using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ManagedHostSshConnectionFactory(IServiceScopeFactory scopeFactory)
{
    public async Task<ManagedHostSshCredentials> ResolveStoredCredentialsAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default) =>
        await ResolveCredentialsAsync(host, ManagedHostSshCredentialRequest.Stored, cancellationToken);

    internal async Task<ManagedHostSshCredentials> ResolveCredentialsAsync(
        ManagedHost host,
        ManagedHostSshCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        ManagedHostSshCredentials? stored = null;
        string? storedFailureMessage = null;

        if (request.PreferStoredCredentials)
        {
            using var scope = scopeFactory.CreateScope();
            var credentialResolver = scope.ServiceProvider.GetRequiredService<ManagedHostSshCredentialResolver>();
            var resolution = await credentialResolver.TryResolveAsync(host, cancellationToken);
            stored = resolution.Credentials;
            storedFailureMessage = resolution.Success ? null : resolution.FailureMessage;
        }

        var username = string.IsNullOrWhiteSpace(request.Username)
            ? stored?.Username ?? host.Username.Trim()
            : request.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("A username is required for SSH connections.");
        }

        var credentials = new ManagedHostSshCredentials(
            username,
            string.IsNullOrWhiteSpace(request.Password) ? stored?.Password : request.Password,
            string.IsNullOrWhiteSpace(request.PrivateKey) ? stored?.PrivateKey : request.PrivateKey,
            string.IsNullOrWhiteSpace(request.PrivateKeyPassphrase) ? stored?.PrivateKeyPassphrase : request.PrivateKeyPassphrase);

        if (string.IsNullOrWhiteSpace(credentials.Password) &&
            string.IsNullOrWhiteSpace(credentials.PrivateKey) &&
            !string.IsNullOrWhiteSpace(storedFailureMessage))
        {
            throw new InvalidOperationException(storedFailureMessage);
        }

        return credentials;
    }

    public ConnectionInfo CreateConnectionInfo(
        ManagedHost host,
        ManagedHostSshCredentials credentials,
        TimeSpan timeout)
    {
        var authMethods = SshAuthenticationBuilder.BuildAuthenticationMethods(host, credentials);
        if (authMethods.Count == 0)
        {
            throw new InvalidOperationException("No supported SSH authentication material was supplied for this host.");
        }

        return new ConnectionInfo(host.Hostname.Trim(), host.Port, credentials.Username.Trim(), authMethods.ToArray())
        {
            Timeout = timeout
        };
    }

    public SshClient CreateSshClient(
        ManagedHost host,
        ManagedHostSshCredentials credentials,
        TimeSpan timeout,
        TimeSpan? keepAliveInterval = null)
    {
        var client = new SshClient(CreateConnectionInfo(host, credentials, timeout));
        if (keepAliveInterval.HasValue)
        {
            client.KeepAliveInterval = keepAliveInterval.Value;
        }

        return client;
    }

    public SftpClient CreateSftpClient(
        ManagedHost host,
        ManagedHostSshCredentials credentials,
        TimeSpan timeout) =>
        new(CreateConnectionInfo(host, credentials, timeout));
}

internal sealed record ManagedHostSshCredentialRequest(
    string? Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase,
    bool PreferStoredCredentials)
{
    public static ManagedHostSshCredentialRequest Stored { get; } =
        new(null, null, null, null, true);
}
