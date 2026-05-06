using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Interfaces;

public interface IUserDisplayPreferenceService
{
    Task<UserDisplayPreference?> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<UserDisplayPreference> SaveAsync(
        Guid userId,
        string themePaletteId,
        string themeMode,
        int fontScalePercent,
        CancellationToken cancellationToken = default);
}
