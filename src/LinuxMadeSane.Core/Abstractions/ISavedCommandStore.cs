using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISavedCommandStore
{
    Task<IReadOnlyList<SavedCommand>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SavedCommand>> ListByHostAsync(Guid hostId, CancellationToken cancellationToken = default);

    Task<SavedCommand?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(SavedCommand command, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
