using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed record CaddyIntegrationDashboardViewModel(
    bool IsInstalled,
    string InstalledVersion,
    bool IsServiceActive,
    bool IsServiceEnabled,
    bool IsManagedImportConfigured,
    bool IsConfigurationValid,
    string ValidationSummary,
    string MainConfigPath,
    string ManagedConfigPath,
    IReadOnlyList<CaddyProxyRouteListItem> Routes,
    IReadOnlyList<OperationLogEntry> LastOperationLogs);
