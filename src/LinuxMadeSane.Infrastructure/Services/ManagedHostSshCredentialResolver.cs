using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ManagedHostSshCredentialResolver
{
    private readonly IServiceScopeFactory? scopeFactory;
    private readonly IHostSecretsService? hostSecretsService;

    public ManagedHostSshCredentialResolver(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    internal ManagedHostSshCredentialResolver(IHostSecretsService hostSecretsService)
    {
        this.hostSecretsService = hostSecretsService;
    }

    public async Task<ManagedHostSshCredentials> ResolveAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default)
    {
        var resolution = await TryResolveAsync(host, cancellationToken);
        if (!resolution.Success || resolution.Credentials is null)
        {
            throw new InvalidOperationException(resolution.FailureMessage);
        }

        return resolution.Credentials;
    }

    public async Task<ManagedHostSshCredentialResolution> TryResolveAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default)
    {
        var username = host.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            return new ManagedHostSshCredentialResolution(
                false,
                null,
                $"Server {host.Name} does not have a stored SSH username.");
        }

        try
        {
            using var scope = scopeFactory?.CreateScope();
            var secrets = scope?.ServiceProvider.GetRequiredService<IHostSecretsService>() ?? hostSecretsService;
            if (secrets is null)
            {
                return new ManagedHostSshCredentialResolution(
                    false,
                    null,
                    $"Stored SSH credentials for {host.Name} could not be resolved.");
            }

            var password = await ResolveOptionalAsync(secrets, host.PasswordSecretReference, cancellationToken);
            var privateKey = await ResolveOptionalAsync(secrets, host.PrivateKeySecretReference, cancellationToken);
            var privateKeyPassphrase = await ResolveOptionalAsync(secrets, host.PrivateKeyPassphraseSecretReference, cancellationToken);

            if (string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(privateKey))
            {
                return new ManagedHostSshCredentialResolution(
                    false,
                    null,
                    $"Server {host.Name} does not have a stored password or private key.");
            }

            return new ManagedHostSshCredentialResolution(
                true,
                new ManagedHostSshCredentials(
                    username,
                    string.IsNullOrWhiteSpace(password) ? null : password,
                    string.IsNullOrWhiteSpace(privateKey) ? null : privateKey,
                    string.IsNullOrWhiteSpace(privateKeyPassphrase) ? null : privateKeyPassphrase),
                string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ManagedHostSshCredentialResolution(
                false,
                null,
                $"Stored SSH credentials for {host.Name} could not be resolved.");
        }
    }

    private static async Task<string?> ResolveOptionalAsync(
        IHostSecretsService hostSecretsService,
        string? secretReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return null;
        }

        return await hostSecretsService.ResolveSecretAsync(secretReference, cancellationToken);
    }
}

public sealed record ManagedHostSshCredentialResolution(
    bool Success,
    ManagedHostSshCredentials? Credentials,
    string FailureMessage);

public sealed record ManagedHostSshCredentials(
    string Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase);
