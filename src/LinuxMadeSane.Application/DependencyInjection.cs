// Copyright (c) Openplan Software.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Application.Services.EdgeGateway;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IAiChatOrchestrationService, AiChatTurnOrchestrator>();
        services.AddScoped<IAiExecutionPlanExecutionService, AiExecutionPlanExecutionService>();
        services.AddScoped<IAiApprovalService, AiApprovalService>();
        services.AddScoped<IAiChatService, AiChatService>();
        services.AddSingleton<IAiPromptSanitizer, AiPromptSanitizer>();
        services.AddScoped<IAiProviderSettingsService, AiProviderSettingsService>();
        services.AddScoped<IUserDisplayPreferenceService, UserDisplayPreferenceService>();
        services.AddScoped<IFileBrowserShortcutService, FileBrowserShortcutService>();
        services.AddScoped<ILocalAiEngineManagerService, LocalAiEngineManagerService>();
        services.AddScoped<LinuxMadeSane.Core.Abstractions.ILocalAiEngineService, LocalAiEngineManagerService>();
        services.AddScoped<ITerminalAiAssistantService, TerminalAiAssistantService>();
        services.AddScoped<IAiThreadService, AiThreadService>();
        services.AddScoped<ISecuritySettingsService, SecuritySettingsService>();
        services.AddScoped<ISecurityAuthenticationService, SecurityAuthenticationService>();
        services.AddScoped<IManagedHostService, ManagedHostService>();
        services.AddScoped<IRunbookService, RunbookService>();
        services.AddScoped<ICaddyIntegrationService, CaddyIntegrationService>();
        services.AddScoped<EdgeGatewayCaddyfileGenerator>();
        services.AddScoped<IEdgeGatewayService, EdgeGatewayService>();
        services.AddScoped<IMediaLibraryIntegrationService, MediaLibraryIntegrationService>();
        services.AddScoped<ISftpServerManagerService, SftpServerManagerService>();
        services.AddScoped<IExposedServiceManager, ExposedServiceManager>();
        services.AddScoped<IShareManagementService, ShareManagementService>();
        services.AddScoped<IServiceOperationsService, ServiceOperationsService>();
        services.AddScoped<IScheduledTaskService, ScheduledTaskService>();
        services.AddScoped<IRdpOptimizationService, RdpOptimizationService>();
        return services;
    }
}
