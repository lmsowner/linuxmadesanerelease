// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
namespace LinuxMadeSane.Core.Models.Portal;

public sealed record PortalLicenseActivationRequest(string Code, string Payload, string Signature);
public sealed record PortalLicenseInstallation(Guid LocalInstanceId, string PublicKey, string DisplayName,
    string MachineName, string AppVersion, string MachineFingerprint, string Channel, DateTimeOffset? IssuedAtUtc = null, string? Nonce = null);
public sealed record PortalLicenseRegistration(Guid InstanceId, Guid OrganizationId, string OrganizationName,
    string ApiKeyId, string ApiSecret, string Product, DateTimeOffset ExpiresAtUtc);
public sealed record PortalAddonManifest(string Version, int PluginApiVersion, string MinimumHostVersion,
    string FileName, long SizeBytes, string Sha256, string DownloadPath);
public sealed record PortalSignedAddonManifest(string Payload, string Signature);
public sealed record PortalLicenseActivationResponse(PortalLicenseRegistration Registration, PortalSignedAddonManifest Addon);

public sealed record PortalManagedRegistrationRequest(Guid TargetLocalInstanceId);
public sealed record PortalManagedRegistrationCode(string Code);
