using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceControlResult(
    Guid ServiceId,
    string UnitName,
    ServiceControlAction Action,
    bool Success,
    string Message);
