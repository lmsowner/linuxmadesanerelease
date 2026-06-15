// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
    ISshHostDiscoveryService sshHostDiscoveryService,
    ICommandExecutionService commandExecutionService) : IManagedHostService
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

    public async Task DeleteHostAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            throw new InvalidOperationException("Host not found.");
        }

        var capabilities = ManagedHostCapabilities.Describe(host);
        if (capabilities.IsLocalLmsHost || IsCurrentLmsEndpoint(host))
        {
            throw new InvalidOperationException("The local Linux Made Sane host cannot be removed from the inventory.");
        }

        if (host.Kind is not ManagedHostKind.SshHost and not ManagedHostKind.LmsHost)
        {
            throw new InvalidOperationException("Only SSH hosts and remote LMS hosts can be removed.");
        }

        await hostStore.DeleteAsync(id, cancellationToken);
        await DeleteStoredHostSecretsAsync(host, cancellationToken);
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

    public async Task<ManagedHostLmsInstallResult> InstallLmsAsync(
        Guid id,
        ManagedHostLmsInstallOptions options,
        IProgress<ManagedHostLmsInstallProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            throw new InvalidOperationException("Host not found.");
        }

        if (IsCurrentLmsEndpoint(host))
        {
            throw new InvalidOperationException("This machine is already the local Linux Made Sane host.");
        }

        var capabilities = ManagedHostCapabilities.Describe(host);
        var isExistingLmsUpdate = capabilities.IsLmsHost && options.UpdateExistingInstall;
        if (!capabilities.SupportsLmsInstall && !isExistingLmsUpdate)
        {
            throw new InvalidOperationException(capabilities.LmsInstallSupportMessage);
        }

        if (!HasStoredConnectionMaterial(host))
        {
            throw new InvalidOperationException("Save SSH credentials for this host before installing Linux Made Sane on it.");
        }

        var sudoPassword = await ResolveStoredSudoPasswordAsync(host, cancellationToken);
        var inputExecutionService = commandExecutionService as ICommandExecutionInputService;
        var canUseStoredSudoPassword =
            !string.IsNullOrEmpty(sudoPassword) &&
            inputExecutionService is not null;
        var commandText = BuildLmsInstallCommand(options, canUseStoredSudoPassword);
        var installProgress = new SynchronousCommandExecutionProgress(update =>
            ReportLmsInstallProgress(
                update,
                progress,
                "Starting remote Linux Made Sane install.",
                "Remote install completed successfully.",
                exitCode => $"Remote install failed with exit code {exitCode}."));

        progress?.Report(new ManagedHostLmsInstallProgressUpdate(
            ManagedHostLmsInstallProgressState.Starting,
            "Connecting over SSH and running the public installer.",
            DateTimeOffset.UtcNow));

        CommandExecutionResult result;
        try
        {
            result = canUseStoredSudoPassword
                ? await inputExecutionService!.ExecuteAsync(
                    host,
                    commandText,
                    new CommandExecutionInput($"{sudoPassword}\n", true),
                    installProgress,
                    cancellationToken)
                : await commandExecutionService.ExecuteAsync(host, commandText, installProgress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                ManagedHostLmsInstallProgressState.Failed,
                ex.Message,
                now));

            return new ManagedHostLmsInstallResult(
                false,
                "Linux Made Sane install failed.",
                ex.Message,
                commandText,
                null,
                string.Empty,
                ex.ToString(),
                now,
                now);
        }

        if (!result.IsSuccess)
        {
            var failureDetail = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The remote installer returned a non-zero exit code."
                : result.StandardError.Trim();

            return new ManagedHostLmsInstallResult(
                false,
                "Linux Made Sane install failed.",
                failureDetail,
                result.CommandText,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.StartedAtUtc,
                result.CompletedAtUtc);
        }

        if (options.MarkAsLmsHostOnSuccess)
        {
            var updatedHost = host with
            {
                Kind = ManagedHostKind.LmsHost,
                LastSeenUtc = result.CompletedAtUtc,
                LastConnectionTestStatus = ConnectionTestStatus.Succeeded
            };
            await hostStore.SaveAsync(updatedHost, cancellationToken);
        }

        var wasLmsHost = ManagedHostCapabilities.IsLmsHost(host);
        return new ManagedHostLmsInstallResult(
            true,
            wasLmsHost ? "Linux Made Sane updated." : "Linux Made Sane installed.",
            wasLmsHost
                ? $"The remote LMS host was updated and restarted. Try http://{host.Hostname}:5080/ if the network and firewall allow browser access."
                : $"The host is now marked as an LMS host. Try http://{host.Hostname}:5080/ if the network and firewall allow browser access.",
            result.CommandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.StartedAtUtc,
            result.CompletedAtUtc);
    }

    public async Task<ManagedHostLmsInstallResult> RegisterLmsHostAsync(
        Guid id,
        IProgress<ManagedHostLmsInstallProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            throw new InvalidOperationException("Host not found.");
        }

        if (IsCurrentLmsEndpoint(host))
        {
            throw new InvalidOperationException("This machine is already the local Linux Made Sane host.");
        }

        if (ManagedHostCapabilities.IsLmsHost(host))
        {
            var now = DateTimeOffset.UtcNow;
            return new ManagedHostLmsInstallResult(
                true,
                "LMS server already registered.",
                "This host is already listed as an LMS host.",
                string.Empty,
                null,
                string.Empty,
                string.Empty,
                now,
                now);
        }

        if (!HasStoredConnectionMaterial(host))
        {
            throw new InvalidOperationException("Save SSH credentials for this host before adding it as an LMS server.");
        }

        var sudoPassword = await ResolveStoredSudoPasswordAsync(host, cancellationToken);
        var inputExecutionService = commandExecutionService as ICommandExecutionInputService;
        var canUseStoredSudoPassword =
            !string.IsNullOrEmpty(sudoPassword) &&
            inputExecutionService is not null;
        var commandText = BuildRemoteLmsDetectionCommand(canUseStoredSudoPassword);
        var detectionProgress = new SynchronousCommandExecutionProgress(update =>
            ReportLmsInstallProgress(
                update,
                progress,
                "Checking the remote host for an existing Linux Made Sane install.",
                "Remote LMS detection completed.",
                exitCode => $"Remote LMS detection failed with exit code {exitCode}."));

        progress?.Report(new ManagedHostLmsInstallProgressUpdate(
            ManagedHostLmsInstallProgressState.Starting,
            "Checking the saved SSH connection for an existing Linux Made Sane install.",
            DateTimeOffset.UtcNow));

        CommandExecutionResult result;
        try
        {
            result = canUseStoredSudoPassword
                ? await inputExecutionService!.ExecuteAsync(
                    host,
                    commandText,
                    new CommandExecutionInput($"{sudoPassword}\n", true),
                    detectionProgress,
                    cancellationToken)
                : await commandExecutionService.ExecuteAsync(host, commandText, detectionProgress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                ManagedHostLmsInstallProgressState.Failed,
                ex.Message,
                now));

            return new ManagedHostLmsInstallResult(
                false,
                "Add LMS Server failed.",
                ex.Message,
                commandText,
                null,
                string.Empty,
                ex.ToString(),
                now,
                now);
        }

        if (!result.IsSuccess)
        {
            var failureDetail = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The remote LMS detection command returned a non-zero exit code."
                : result.StandardError.Trim();

            return new ManagedHostLmsInstallResult(
                false,
                "Add LMS Server failed.",
                failureDetail,
                result.CommandText,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.StartedAtUtc,
                result.CompletedAtUtc);
        }

        if (!IsRemoteLmsDetected(result.StandardOutput))
        {
            var detail = "No Linux Made Sane service, install directory, config, or data store was detected on this host.";
            progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                ManagedHostLmsInstallProgressState.Failed,
                detail,
                result.CompletedAtUtc));

            return new ManagedHostLmsInstallResult(
                false,
                "No LMS install detected.",
                detail,
                result.CommandText,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.StartedAtUtc,
                result.CompletedAtUtc);
        }

        var updatedHost = host with
        {
            Kind = ManagedHostKind.LmsHost,
            LastSeenUtc = result.CompletedAtUtc,
            LastConnectionTestStatus = ConnectionTestStatus.Succeeded
        };
        await hostStore.SaveAsync(updatedHost, cancellationToken);

        var version = ParseRemoteLmsVersion(result.StandardOutput);
        var versionDetail = string.IsNullOrWhiteSpace(version)
            ? "The remote install was detected."
            : $"The remote LMS install was detected as version {version}.";

        progress?.Report(new ManagedHostLmsInstallProgressUpdate(
            ManagedHostLmsInstallProgressState.Completed,
            "Linux Made Sane install found; host registered as an LMS server.",
            result.CompletedAtUtc));

        return new ManagedHostLmsInstallResult(
            true,
            "LMS server added.",
            $"{versionDetail} Try http://{host.Hostname}:5080/ if the network and firewall allow browser access.",
            result.CommandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.StartedAtUtc,
            result.CompletedAtUtc);
    }

    public async Task<ManagedHostLmsInstallResult> UninstallLmsAsync(
        Guid id,
        ManagedHostLmsUninstallOptions options,
        IProgress<ManagedHostLmsInstallProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = await hostStore.GetAsync(id, cancellationToken);
        if (host is null)
        {
            throw new InvalidOperationException("Host not found.");
        }

        if (IsCurrentLmsEndpoint(host))
        {
            throw new InvalidOperationException("Use the local installer command to uninstall the local Linux Made Sane instance.");
        }

        if (!ManagedHostCapabilities.IsLmsHost(host))
        {
            throw new InvalidOperationException("This host is not registered as a Linux Made Sane host.");
        }

        if (!HasStoredConnectionMaterial(host))
        {
            throw new InvalidOperationException("Save SSH credentials for this host before uninstalling Linux Made Sane from it.");
        }

        var sudoPassword = await ResolveStoredSudoPasswordAsync(host, cancellationToken);
        var inputExecutionService = commandExecutionService as ICommandExecutionInputService;
        var canUseStoredSudoPassword =
            !string.IsNullOrEmpty(sudoPassword) &&
            inputExecutionService is not null;
        var commandText = BuildLmsUninstallCommand(options, canUseStoredSudoPassword);
        var uninstallProgress = new SynchronousCommandExecutionProgress(update =>
            ReportLmsInstallProgress(
                update,
                progress,
                "Starting remote Linux Made Sane uninstall.",
                "Remote uninstall completed successfully.",
                exitCode => $"Remote uninstall failed with exit code {exitCode}."));

        progress?.Report(new ManagedHostLmsInstallProgressUpdate(
            ManagedHostLmsInstallProgressState.Starting,
            "Connecting over SSH and running the public uninstaller.",
            DateTimeOffset.UtcNow));

        CommandExecutionResult result;
        try
        {
            result = canUseStoredSudoPassword
                ? await inputExecutionService!.ExecuteAsync(
                    host,
                    commandText,
                    new CommandExecutionInput($"{sudoPassword}\n", true),
                    uninstallProgress,
                    cancellationToken)
                : await commandExecutionService.ExecuteAsync(host, commandText, uninstallProgress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                ManagedHostLmsInstallProgressState.Failed,
                ex.Message,
                now));

            return new ManagedHostLmsInstallResult(
                false,
                "Linux Made Sane uninstall failed.",
                ex.Message,
                commandText,
                null,
                string.Empty,
                ex.ToString(),
                now,
                now);
        }

        if (!result.IsSuccess)
        {
            var failureDetail = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The remote uninstaller returned a non-zero exit code."
                : result.StandardError.Trim();

            return new ManagedHostLmsInstallResult(
                false,
                "Linux Made Sane uninstall failed.",
                failureDetail,
                result.CommandText,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.StartedAtUtc,
                result.CompletedAtUtc);
        }

        if (options.MarkAsSshHostOnSuccess)
        {
            var updatedHost = host with
            {
                Kind = ManagedHostKind.SshHost,
                LastSeenUtc = result.CompletedAtUtc,
                LastConnectionTestStatus = ConnectionTestStatus.Succeeded
            };
            await hostStore.SaveAsync(updatedHost, cancellationToken);
        }

        return new ManagedHostLmsInstallResult(
            true,
            "Linux Made Sane uninstalled.",
            options.RemoveData
                ? "The remote LMS service, application files, data, and config were removed. The host remains registered as an SSH host."
                : "The remote LMS service and application files were removed. Data and config were kept. The host remains registered as an SSH host.",
            result.CommandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.StartedAtUtc,
            result.CompletedAtUtc);
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
            NormalizeHostKind(editor.HostKind, hostId, editor.Hostname));
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

    private async Task<string?> ResolveStoredSudoPasswordAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host.PasswordSecretReference))
        {
            return null;
        }

        var password = await secretStore.ResolveSecretAsync(host.PasswordSecretReference, cancellationToken);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    private static string BuildLmsInstallCommand(
        ManagedHostLmsInstallOptions options,
        bool canUseStoredSudoPassword)
    {
        var installUrl = NormalizeInstallUrl(options.InstallUrl);
        var installerArguments = new List<string>();
        if (options.UpdateExistingInstall)
        {
            installerArguments.Add("--install");
        }

        if (options.StartService)
        {
            installerArguments.Add("--start");
        }
        else
        {
            installerArguments.Add("--no-start");
        }

        if (!options.ConfigureLocalSshRunner)
        {
            installerArguments.Add("--no-local-ssh");
        }
        else if (!options.EnableLocalSudo)
        {
            installerArguments.Add("--no-local-sudo");
        }

        return BuildRemoteLmsInstallerCommand(
            installUrl,
            "lms-host-conversion",
            installerArguments,
            canUseStoredSudoPassword,
            options.StartService);
    }

    private static string BuildLmsUninstallCommand(
        ManagedHostLmsUninstallOptions options,
        bool canUseStoredSudoPassword)
    {
        var installUrl = NormalizeInstallUrl(options.InstallUrl);
        var installerArguments = options.RemoveData
            ? new List<string> { "--purge" }
            : new List<string> { "--uninstall", "--keep-data" };

        return BuildRemoteLmsInstallerCommand(
            installUrl,
            "lms-host-uninstall",
            installerArguments,
            canUseStoredSudoPassword,
            false);
    }

    private static string BuildRemoteLmsDetectionCommand(bool canUseStoredSudoPassword)
    {
        var script = string.Join(
            "\n",
            "set -u",
            "LMS_SERVICE_UNIT='linux-made-sane.service'",
            "LMS_CURRENT_DIR='/opt/linuxmadesane/ce/current'",
            "LMS_CONFIG_ROOT='/etc/linuxmadesane/ce'",
            "LMS_DATA_ROOT='/var/lib/linuxmadesane/ce'",
            "SUDO=''",
            "if [ \"$(id -u)\" -ne 0 ] && command -v sudo >/dev/null 2>&1; then",
            canUseStoredSudoPassword
                ? "  if IFS= read -r LMS_SUDO_PASSWORD; then\n    if printf '%s\\n' \"$LMS_SUDO_PASSWORD\" | sudo -S -p '' -v >/dev/null 2>&1; then\n      SUDO='sudo -n'\n    fi\n    unset LMS_SUDO_PASSWORD\n  fi"
                : "  if sudo -n true >/dev/null 2>&1; then\n    SUDO='sudo -n'\n  fi",
            "fi",
            "lms_marker_found=false",
            "if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then",
            "  if systemctl list-unit-files \"$LMS_SERVICE_UNIT\" --no-legend --no-pager 2>/dev/null | grep -q \"^$LMS_SERVICE_UNIT\"; then",
            "    echo \"LMS_SERVICE_UNIT=$LMS_SERVICE_UNIT\"",
            "    lms_marker_found=true",
            "  elif systemctl list-units --all \"$LMS_SERVICE_UNIT\" --no-legend --no-pager 2>/dev/null | grep -q \"^$LMS_SERVICE_UNIT\"; then",
            "    echo \"LMS_SERVICE_UNIT=$LMS_SERVICE_UNIT\"",
            "    lms_marker_found=true",
            "  fi",
            "  if systemctl is-active --quiet \"$LMS_SERVICE_UNIT\" >/dev/null 2>&1; then",
            "    echo 'LMS_SERVICE_ACTIVE=true'",
            "  fi",
            "fi",
            "for path in \"$LMS_CURRENT_DIR\" \"$LMS_CONFIG_ROOT/service.env\" \"$LMS_DATA_ROOT/linuxmadesane.db\" '/etc/systemd/system/linux-made-sane.service' '/opt/linuxmadesane/pro/current' '/etc/linuxmadesane/pro/service.env' '/var/lib/linuxmadesane/pro/linuxmadesane.db'; do",
            "  if [ -e \"$path\" ] || { [ -n \"$SUDO\" ] && $SUDO test -e \"$path\" 2>/dev/null; }; then",
            "    echo \"LMS_MARKER=$path\"",
            "    lms_marker_found=true",
            "  fi",
            "done",
            "for version_path in \"$LMS_CURRENT_DIR/version.txt\" '/opt/linuxmadesane/pro/current/version.txt'; do",
            "  if [ -r \"$version_path\" ]; then",
            "    printf 'LMS_VERSION='",
            "    sed -n '1p' \"$version_path\" | tr -d '\\r'",
            "    break",
            "  elif [ -n \"$SUDO\" ] && $SUDO test -r \"$version_path\" 2>/dev/null; then",
            "    printf 'LMS_VERSION='",
            "    $SUDO sed -n '1p' \"$version_path\" | tr -d '\\r'",
            "    break",
            "  fi",
            "done",
            "if [ \"$lms_marker_found\" = true ]; then",
            "  echo 'LMS_DETECTED=true'",
            "else",
            "  echo 'LMS_DETECTED=false'",
            "fi");

        return $"bash -lc {QuoteShellArgument(script)}";
    }

    private static string BuildRemoteLmsInstallerCommand(
        string installUrl,
        string source,
        IReadOnlyCollection<string> installerArguments,
        bool canUseStoredSudoPassword,
        bool expectServiceActive)
    {
        var quotedInstallerArguments = string.Join(" ", installerArguments.Select(QuoteShellArgument));
        var expectServiceActiveValue = expectServiceActive ? "true" : "false";
        var script = string.Join(
            "\n",
            "set -euo pipefail",
            "REMOTE_LMS_SERVICE_UNIT='linux-made-sane.service'",
            "REMOTE_LMS_CURRENT_DIR='/opt/linuxmadesane/ce/current'",
            "REMOTE_LMS_PREVIOUS_CURRENT=''",
            "REMOTE_LMS_SERVICE_WAS_ACTIVE=false",
            "if [ \"$(id -u)\" -eq 0 ]; then",
            "  SUDO=''",
            "elif command -v sudo >/dev/null 2>&1; then",
            canUseStoredSudoPassword
                ? "  if IFS= read -r LMS_SUDO_PASSWORD; then\n    printf '%s\\n' \"$LMS_SUDO_PASSWORD\" | sudo -S -p '' -v\n    unset LMS_SUDO_PASSWORD\n    SUDO='sudo -n'\n  else\n    echo 'Stored SSH password was not available to sudo.' >&2\n    exit 126\n  fi"
                : "  SUDO='sudo -n'",
            "else",
            "  echo 'Remote account is not root and sudo is not installed.' >&2",
            "  exit 126",
            "fi",
            "if ! command -v curl >/dev/null 2>&1; then",
            "  if command -v apt-get >/dev/null 2>&1; then",
            "    $SUDO apt-get update",
            "    $SUDO apt-get install -y -- curl",
            "  else",
            "    echo 'curl is required to install or remove Linux Made Sane. Install curl or use an apt-based distro.' >&2",
            "    exit 127",
            "  fi",
            "fi",
            "if [ -e \"$REMOTE_LMS_CURRENT_DIR\" ] || [ -L \"$REMOTE_LMS_CURRENT_DIR\" ]; then",
            "  REMOTE_LMS_PREVIOUS_CURRENT=\"$(readlink -f \"$REMOTE_LMS_CURRENT_DIR\" 2>/dev/null || true)\"",
            "fi",
            "if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ] && $SUDO systemctl is-active --quiet \"$REMOTE_LMS_SERVICE_UNIT\"; then",
            "  REMOTE_LMS_SERVICE_WAS_ACTIVE=true",
            "fi",
            "rollback_remote_lms() {",
            "  local reason=\"$1\"",
            "  echo \"Remote LMS update failed: $reason\" >&2",
            "  if [ -n \"$REMOTE_LMS_PREVIOUS_CURRENT\" ] && [ -d \"$REMOTE_LMS_PREVIOUS_CURRENT\" ]; then",
            "    echo \"Rolling remote LMS back to $REMOTE_LMS_PREVIOUS_CURRENT\" >&2",
            "    $SUDO ln -sfn \"$REMOTE_LMS_PREVIOUS_CURRENT\" \"$REMOTE_LMS_CURRENT_DIR\" || true",
            "  fi",
            "  if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ] && { [ \"$REMOTE_LMS_SERVICE_WAS_ACTIVE\" = true ] || [ -n \"$REMOTE_LMS_PREVIOUS_CURRENT\" ]; }; then",
            "    $SUDO systemctl daemon-reload >/dev/null 2>&1 || true",
            "    $SUDO systemctl restart \"$REMOTE_LMS_SERVICE_UNIT\" >/dev/null 2>&1 || true",
            "  fi",
            "}",
            "verify_remote_lms_active() {",
            "  command -v systemctl >/dev/null 2>&1 || return 0",
            "  [ -d /run/systemd/system ] || return 0",
            "  for _ in $(seq 1 30); do",
            "    if $SUDO systemctl is-active --quiet \"$REMOTE_LMS_SERVICE_UNIT\"; then",
            "      return 0",
            "    fi",
            "    sleep 1",
            "  done",
            "  $SUDO systemctl --no-pager --full status \"$REMOTE_LMS_SERVICE_UNIT\" >&2 || true",
            "  return 1",
            "}",
            $"if ! curl -fsSL {QuoteShellArgument(installUrl)} | $SUDO env LMS_SOURCE={source} bash -s -- {quotedInstallerArguments}; then",
            "  rollback_remote_lms 'installer returned a non-zero exit code'",
            "  exit 1",
            "fi",
            $"if [ '{expectServiceActiveValue}' = true ] && ! verify_remote_lms_active; then",
            "  rollback_remote_lms 'service did not become active after install'",
            "  exit 1",
            "fi");

        return $"bash -lc {QuoteShellArgument(script)}";
    }

    private static string NormalizeInstallUrl(string installUrl)
    {
        if (!Uri.TryCreate(installUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS installer URL.");
        }

        return uri.ToString();
    }

    private static string QuoteShellArgument(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static bool IsRemoteLmsDetected(string output) =>
        output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, "LMS_DETECTED=true", StringComparison.Ordinal));

    private static string? ParseRemoteLmsVersion(string output) =>
        output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.StartsWith("LMS_VERSION=", StringComparison.Ordinal)
                ? line["LMS_VERSION=".Length..].Trim()
                : null)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));

    private static void ReportLmsInstallProgress(
        CommandExecutionUpdate update,
        IProgress<ManagedHostLmsInstallProgressUpdate>? progress,
        string startedMessage,
        string completedMessage,
        Func<int, string> failedMessageFactory)
    {
        switch (update)
        {
            case CommandExecutionStartedUpdate started:
                progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                    ManagedHostLmsInstallProgressState.Starting,
                    startedMessage,
                    started.StartedAtUtc));
                break;
            case CommandExecutionOutputUpdate output when !string.IsNullOrWhiteSpace(output.Content):
                progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                    ManagedHostLmsInstallProgressState.Output,
                    output.Content.TrimEnd(),
                    output.OccurredAtUtc,
                    output.Channel));
                break;
            case CommandExecutionCompletedUpdate completed:
                progress?.Report(new ManagedHostLmsInstallProgressUpdate(
                    completed.ExitCode == 0
                        ? ManagedHostLmsInstallProgressState.Completed
                        : ManagedHostLmsInstallProgressState.Failed,
                    completed.ExitCode == 0
                        ? completedMessage
                        : failedMessageFactory(completed.ExitCode),
                    completed.CompletedAtUtc));
                break;
        }
    }

    private static bool HasStoredConnectionMaterial(ManagedHost host) =>
        !string.IsNullOrWhiteSpace(host.PasswordSecretReference) ||
        !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference);

    private static bool IsCurrentLmsEndpoint(ManagedHost host) =>
        AiLocalMachine.IsLocalMachine(host.Id) ||
        AiLocalMachine.IsLoopbackHostname(host.Hostname);

    private async Task DeleteTransientSecretAsync(string? secretReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return;
        }

        await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
    }

    private async Task DeleteStoredHostSecretsAsync(ManagedHost host, CancellationToken cancellationToken)
    {
        await DeleteTransientSecretAsync(host.PasswordSecretReference, cancellationToken);
        await DeleteTransientSecretAsync(host.PrivateKeySecretReference, cancellationToken);
        await DeleteTransientSecretAsync(host.PrivateKeyPassphraseSecretReference, cancellationToken);
    }

    private static string NormalizeEnvironment(string? environment)
    {
        var trimmedEnvironment = environment?.Trim();
        return string.IsNullOrWhiteSpace(trimmedEnvironment)
            ? "Unlabeled"
            : trimmedEnvironment;
    }

    private static ManagedHostKind NormalizeHostKind(ManagedHostKind hostKind, Guid hostId, string hostname) =>
        AiLocalMachine.IsLocalMachine(hostId) || AiLocalMachine.IsLoopbackHostname(hostname)
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

    private sealed class SynchronousCommandExecutionProgress(Action<CommandExecutionUpdate> handler) : IProgress<CommandExecutionUpdate>
    {
        public void Report(CommandExecutionUpdate value) => handler(value);
    }
}
