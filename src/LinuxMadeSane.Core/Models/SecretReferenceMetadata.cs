namespace LinuxMadeSane.Core.Models;

public sealed record SecretReferenceMetadata(
    string Reference,
    string Purpose,
    bool RequiresProtectedStorage);
