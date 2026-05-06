using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Contracts.Cloudflare;

public sealed record CloudflareExposeServiceWorkspaceViewModel(
    Guid HostId,
    string HostName,
    string HostDescription,
    CloudflareSettings? SavedSettings,
    IReadOnlyList<CloudflareExposedServiceListItemViewModel> ServiceEntries,
    IReadOnlyList<string> SyncWarnings,
    CloudflaredConnectorStatus? HostConnectorStatus);
