namespace LinuxMadeSane.Core.Models;

public static class SecuritySessionPolicy
{
    public const int MinimumSessionLifetimeMinutes = 5;
    public const int DefaultSessionLifetimeMinutes = 60;
    public const int MaximumSessionLifetimeMinutes = 43_200;

    public static int NormalizeSessionLifetimeMinutes(int minutes) =>
        minutes <= 0
            ? DefaultSessionLifetimeMinutes
            : Math.Clamp(minutes, MinimumSessionLifetimeMinutes, MaximumSessionLifetimeMinutes);
}
