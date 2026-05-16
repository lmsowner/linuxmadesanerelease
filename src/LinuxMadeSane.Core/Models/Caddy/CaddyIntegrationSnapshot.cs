// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
