namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposureWarning(
    string Code,
    string Message,
    bool RequiresConfirmation);
