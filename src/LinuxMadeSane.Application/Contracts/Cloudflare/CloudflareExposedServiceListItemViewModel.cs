using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Contracts.Cloudflare;

public sealed record CloudflareExposedServiceListItemViewModel(
    ExposedServiceConfig Config,
    bool ExistsInLmsStore,
    bool ExistsInCloudflare,
    bool IsOutOfSync,
    string SyncSummary);
