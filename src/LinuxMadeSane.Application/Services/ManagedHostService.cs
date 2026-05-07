using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Services;

// Guardrail: this service owns managed-host lifecycle and health only. Runbook library
// and execution live in RunbookService so host operations stay predictable and testable.
public sealed class ManagedHostService(
    IManagedHostStore hostStore,
    ISavedCommandStore savedCommandStore,
    ISshConnectionService sshConnectionService,
    IManagedHostHealthProbe healthProbe,
    ISecretStore secretStore,
    ISshHostDiscoveryService sshHostDiscoveryService) : IManagedHostService
{
    private static readonly IReadOnlyList<SshCredentialType> CredentialTypes =
    [
        new(AuthenticationType.Password, "Password", "Username and password for direct SSH login.", true),
        new(AuthenticationType.PasswordAndPrivateKey, "Password + public key", "Supports SSH setups that require both a password and key-based auth.", true),
        new(AuthenticationType.PrivateKey, "Public key", "Authenticates with a client private key that matches a server-side authorized public key.", true)
    ];

    public Task<IReadOnlyList<ManagedHost>> ListHostsAsync(CancellationToken cancellationToken = default) =>
        hostStore.ListAsync(cancellationToken);

    public async Task<ManagedHostEditor?> GetEditorAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        return host is null ? null : MapEditor(host);
    }

    public async Task<Guid> SaveHostAsync(ManagedHostEditor editor, CancellationToken cancellationToken = default)
    {
        var hostId = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue ? await hostStore.GetAsync(hostId, cancellationToken) : null;
        var normalizedUsername = editor.Username.Trim();
        var passwordSecretReference = await ResolveSecretReferenceAsync(
            editor.Password,
            editor.ClearStoredPassword,
            existing?.PasswordSecretReference,
            "managed-host-password",
            cancellationToken);
        var privateKeySecretReference = await ResolveSecretReferenceAsync(
            editor.PrivateKey,
            editor.ClearStoredPrivateKey,
            existing?.PrivateKeySecretReference,
            "managed-host-private-key",
            cancellationToken);
        var privateKeyPassphraseSecretReference = await ResolveSecretReferenceAsync(
            editor.PrivateKeyPassphrase,
            editor.ClearStoredPrivateKeyPassphrase,
            existing?.PrivateKeyPassphraseSecretReference,
            "managed-host-private-key-passphrase",
            cancellationToken);

        var host = BuildManagedHost(
            editor,
            hostId,
            existing,
            passwordSecretReference,
            privateKeySecretReference,
            privateKeyPassphraseSecretReference,
            normalizedUsername,
            editor.Name.Trim());

        await hostStore.SaveAsync(host, cancellationToken);
        return hostId;
    }

    public async Task<HostConnectionTestResult> TestConnectionAsync(
        ManagedHostEditor editor,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionEditor(editor);

        var existing = editor.Id.HasValue
            ? await hostStore.GetAsync(editor.Id.Value, cancellationToken)
            : null;

        string? transientPasswordReference = null;
        string? transientPrivateKeyReference = null;
        string? transientPrivateKeyPassphraseReference = null;

        try
        {
            var passwordSecretReference = existing?.PasswordSecretReference;
            if (editor.ClearStoredPassword)
            {
                passwordSecretReference = null;
            }

            if (!string.IsNullOrWhiteSpace(editor.Password))
            {
                transientPasswordReference = await secretStore.StoreSecretAsync(
                    editor.Password,
                    "managed-host-test-password",
                    cancellationToken);
                passwordSecretReference = transientPasswordReference;
            }

            var privateKeySecretReference = existing?.PrivateKeySecretReference;
            if (editor.ClearStoredPrivateKey)
            {
                privateKeySecretReference = null;
            }

            if (!string.IsNullOrWhiteSpace(editor.PrivateKey))
            {
                transientPrivateKeyReference = await secretStore.StoreSecretAsync(
                    editor.PrivateKey,
                    "managed-host-test-private-key",
                    cancellationToken);
                privateKeySecretReference = transientPrivateKeyReference;
            }

            var privateKeyPassphraseSecretReference = existing?.PrivateKeyPassphraseSecretReference;
            if (editor.ClearStoredPrivateKeyPassphrase)
            {
                privateKeyPassphraseSecretReference = null;
            }

            if (!string.IsNullOrWhiteSpace(editor.PrivateKeyPassphrase))
            {
                transientPrivateKeyPassphraseReference = await secretStore.StoreSecretAsync(
                    editor.PrivateKeyPassphrase,
                    "managed-host-test-private-key-passphrase",
                    cancellationToken);
                privateKeyPassphraseSecretReference = transientPrivateKeyPassphraseReference;
            }

            var host = BuildManagedHost(
                editor,
                editor.Id ?? Guid.NewGuid(),
                existing,
                passwordSecretReference,
                privateKeySecretReference,
                privateKeyPassphraseSecretReference,
                editor.Username.Trim(),
                BuildTestDisplayName(editor));

            return await sshConnectionService.TestConnectionAsync(host, cancellationToken);
        }
        finally
        {
            await DeleteTransientSecretAsync(transientPasswordReference, cancellationToken);
            await DeleteTransientSecretAsync(transientPrivateKeyReference, cancellationToken);
            await DeleteTransientSecretAsync(transientPrivateKeyPassphraseReference, cancellationToken);
        }
    }

    public async Task<HostConnectionTestResult> TestConnectionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            return new HostConnectionTestResult(
                ConnectionTestStatus.NotRun,
                "Host not found.",
                null,
                DateTimeOffset.UtcNow);
        }

        return await GetConnectionResultAsync(host, cancellationToken);
    }

    public Task<HostConnectionTestResult> TestConnectionAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default) =>
        GetConnectionResultAsync(host, cancellationToken);

    public Task<SshHostDiscoveryResult> DiscoverHostsAsync(
        SshHostDiscoveryScope scope,
        IProgress<SshHostDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        sshHostDiscoveryService.DiscoverHostsAsync(scope, progress, cancellationToken);

    public Task<SshHostDiscoveryResult> GetCachedHostDiscoveryAsync(
        SshHostDiscoveryScope scope,
        CancellationToken cancellationToken = default) =>
        sshHostDiscoveryService.GetCachedHostsAsync(scope, cancellationToken);

    public async Task<ManagedHostDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            return null;
        }

        var commands = await savedCommandStore.ListByHostAsync(id, cancellationToken);
        var connection = await GetConnectionResultAsync(host, cancellationToken);
        var health = await BuildHealthSnapshotAsync(host, connection, cancellationToken);
        var capabilities = ManagedHostCapabilities.Describe(host);

        return new ManagedHostDetailsViewModel(host, connection, health, commands, CredentialTypes, capabilities);
    }

    public async Task<ServerHealthSnapshot> GetHealthSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            return BuildUnavailableHealthSnapshot(DateTimeOffset.UtcNow);
        }

        return await BuildHealthSnapshotAsync(host, cancellationToken);
    }

    public Task<ServerHealthSnapshot> GetHealthSnapshotAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default) =>
        BuildHealthSnapshotAsync(host, cancellationToken);

    public async Task<SftpBrowserViewModel?> GetSftpBrowserAsync(Guid id, string? path, CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            return null;
        }

        var currentPath = ManagedHostPathSupport.NormalizeWorkingDirectory(
            string.IsNullOrWhiteSpace(path) ? host.DefaultWorkingDirectory : path.Trim(),
            host.Username);
        return new SftpBrowserViewModel(host, currentPath, Array.Empty<SftpItem>());
    }

    private static ManagedHostEditor MapEditor(ManagedHost host) =>
        new()
        {
            Id = host.Id,
            Name = host.Name,
            Hostname = host.Hostname,
            Port = host.Port,
            Environment = host.Environment,
            Description = host.Description,
            DefaultWorkingDirectory = host.DefaultWorkingDirectory,
            OperatingStatus = host.OperatingStatus,
            PrimaryAuthenticationType = host.PrimaryAuthenticationType,
            FallbackAuthenticationType = host.FallbackAuthenticationType,
            Username = host.Username,
            UseKeyboardInteractiveFallback = host.UseKeyboardInteractiveFallback,
            Platform = host.Platform,
            HostKind = host.Kind,
            HasStoredPassword = !string.IsNullOrWhiteSpace(host.PasswordSecretReference),
            HasStoredPrivateKey = !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference),
            HasStoredPrivateKeyPassphrase = !string.IsNullOrWhiteSpace(host.PrivateKeyPassphraseSecretReference)
        };

    private async Task<string?> ResolveSecretReferenceAsync(
        string? secretValue,
        bool clearExisting,
        string? existingSecretReference,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (clearExisting)
        {
            if (!string.IsNullOrWhiteSpace(existingSecretReference))
            {
                await secretStore.DeleteSecretAsync(existingSecretReference, cancellationToken);
            }

            existingSecretReference = null;
        }

        if (string.IsNullOrWhiteSpace(secretValue))
        {
            return existingSecretReference;
        }

        var newReference = await secretStore.StoreSecretAsync(secretValue, purpose, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingSecretReference))
        {
            await secretStore.DeleteSecretAsync(existingSecretReference, cancellationToken);
        }

        return newReference;
    }

    private static ManagedHost BuildManagedHost(
        ManagedHostEditor editor,
        Guid hostId,
        ManagedHost? existing,
        string? passwordSecretReference,
        string? privateKeySecretReference,
        string? privateKeyPassphraseSecretReference,
        string normalizedUsername,
        string displayName)
    {
        var defaultWorkingDirectory = ManagedHostPathSupport.NormalizeWorkingDirectory(editor.DefaultWorkingDirectory, normalizedUsername);

        return new ManagedHost(
            hostId,
            string.IsNullOrWhiteSpace(displayName) ? "Managed host" : displayName,
            editor.Hostname.Trim(),
            editor.Port,
            NormalizeEnvironment(editor.Environment),
            editor.Description.Trim(),
            defaultWorkingDirectory,
            editor.OperatingStatus,
            editor.PrimaryAuthenticationType,
            editor.FallbackAuthenticationType,
            normalizedUsername,
            passwordSecretReference,
            privateKeySecretReference,
            privateKeyPassphraseSecretReference,
            editor.UseKeyboardInteractiveFallback,
            existing?.LastSeenUtc,
            existing?.LastConnectionTestStatus ?? ConnectionTestStatus.NotRun,
            editor.Platform.Trim(),
            NormalizeHostKind(editor.HostKind, hostId));
    }

    private static void ValidateConnectionEditor(ManagedHostEditor editor)
    {
        if (string.IsNullOrWhiteSpace(editor.Hostname))
        {
            throw new InvalidOperationException("Enter a hostname before testing the connection.");
        }

        if (editor.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Enter a valid SSH port before testing the connection.");
        }

        if (string.IsNullOrWhiteSpace(editor.Username))
        {
            throw new InvalidOperationException("Enter a username before testing the connection.");
        }
    }

    private static string BuildTestDisplayName(ManagedHostEditor editor)
    {
        var normalizedName = editor.Name.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedName;
        }

        var normalizedHostname = editor.Hostname.Trim();
        return string.IsNullOrWhiteSpace(normalizedHostname)
            ? "Connection test"
            : normalizedHostname;
    }

    private async Task DeleteTransientSecretAsync(string? secretReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return;
        }

        await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
    }

    private static string NormalizeEnvironment(string? environment)
    {
        var trimmedEnvironment = environment?.Trim();
        return string.IsNullOrWhiteSpace(trimmedEnvironment)
            ? "Unlabeled"
            : trimmedEnvironment;
    }

    private static ManagedHostKind NormalizeHostKind(ManagedHostKind hostKind, Guid hostId) =>
        AiLocalMachine.IsLocalMachine(hostId)
            ? ManagedHostKind.LmsHost
            : hostKind;

    private async Task<HostConnectionTestResult> GetConnectionResultAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        HostConnectionTestResult connection;

        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            connection = new HostConnectionTestResult(
                ConnectionTestStatus.Succeeded,
                "Local Linux Made Sane host.",
                "Live health is collected directly from this machine.",
                DateTimeOffset.UtcNow);
        }
        else
        {
            connection = await sshConnectionService.TestConnectionAsync(host, cancellationToken);
        }

        await RecordConnectionResultAsync(host, connection, cancellationToken);
        return connection;
    }

    private Task RecordConnectionResultAsync(
        ManagedHost host,
        HostConnectionTestResult connection,
        CancellationToken cancellationToken)
    {
        var lastSeenUtc = connection.Status == ConnectionTestStatus.Succeeded
            ? connection.CheckedAtUtc
            : host.LastSeenUtc;

        var updatedHost = host with
        {
            LastSeenUtc = lastSeenUtc,
            LastConnectionTestStatus = connection.Status
        };

        return hostStore.SaveAsync(updatedHost, cancellationToken);
    }

    private async Task<ServerHealthSnapshot> BuildHealthSnapshotAsync(
        ManagedHost host,
        HostConnectionTestResult connection,
        CancellationToken cancellationToken)
    {
        if (connection.Status != ConnectionTestStatus.Succeeded)
        {
            return BuildUnavailableHealthSnapshot(connection.CheckedAtUtc);
        }

        return await BuildHealthSnapshotAsync(host, cancellationToken);
    }

    private async Task<ServerHealthSnapshot> BuildHealthSnapshotAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            return await healthProbe.GetSnapshotAsync(host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return BuildUnavailableHealthSnapshot(DateTimeOffset.UtcNow);
        }
    }

    private static ServerHealthSnapshot BuildUnavailableHealthSnapshot(DateTimeOffset capturedAtUtc) =>
        new(
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            "Unavailable",
            "Unavailable",
            capturedAtUtc);
}
