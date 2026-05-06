namespace LinuxMadeSane.Core.Models.Caddy;

public sealed record CaddyIntegrationSnapshot(
    bool IsInstalled,
    string InstalledVersion,
    bool IsServiceActive,
    bool IsServiceEnabled,
    bool IsManagedImportConfigured,
    bool IsConfigurationValid,
    string ValidationSummary,
    string MainConfigPath,
    string ManagedConfigPath,
    IReadOnlyList<CaddyProxyRouteDefinition> Routes);
