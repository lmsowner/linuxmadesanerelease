// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
        services.AddSingleton<FileTextThumbnailCache>();
        services.AddHostedService<FileThumbnailCacheCleanupService>();
        services.AddScoped<EmailMfaAuthenticationService>();
        services.AddScoped<LocalAccessRecoveryService>();
        services.AddScoped<InitialSetupAccessService>();
        services.AddSingleton<TemporarySetupAuthorizationService>();
        services.AddSingleton<DesktopHelperSetupService>();
        return services;
    }
}
