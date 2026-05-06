using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class StoredHostSecretsService(ISecretStore secretStore) : IHostSecretsService
{
    public Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default) =>
        secretStore.ResolveSecretAsync(secretReference, cancellationToken);

    public Task<string> StoreSecretAsync(string secretValue, CancellationToken cancellationToken = default) =>
        secretStore.StoreSecretAsync(secretValue, "managed-host", cancellationToken);
}
