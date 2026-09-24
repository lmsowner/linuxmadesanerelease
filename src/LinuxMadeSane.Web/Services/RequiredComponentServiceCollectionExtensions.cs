// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Web.Services;

internal static class RequiredComponentServiceCollectionExtensions
{
    public static IServiceCollection AddRequiredComponentServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        services.AddSingleton(_ => FileThumbnailCacheOptions.FromConfiguration(configuration, contentRootPath));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IFileThumbnailRenderer, FfmpegFileThumbnailRenderer>();
        services.AddSingleton<FileThumbnailCacheService>();
        services.AddHostedService<FileThumbnailCacheCleanupService>();
        services.AddScoped<EmailMfaAuthenticationService>();
        services.AddScoped<LocalAccessRecoveryService>();
        services.AddSingleton<TemporarySetupAuthorizationService>();
        services.AddSingleton<DesktopHelperSetupService>();
        return services;
    }
}
