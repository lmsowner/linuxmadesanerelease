// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed class EdgeGatewayOptions
{
    public int LmsForwardAuthPort { get; set; } = 5080;
    public string PublicLoginBaseUrl { get; set; } = string.Empty;
    public string CaddyLocalServiceUrl { get; set; } = "http://localhost:8443";
    public string GatewaySubdomain { get; set; } = string.Empty;
    public string[] TrustedProxyCidrs { get; set; } = ["127.0.0.1/32", "::1/128"];
    public int TemporaryIpApprovalIdleTimeoutMinutes { get; set; } = 15;
    public int TemporaryIpApprovalMaxLifetimeMinutes { get; set; } = 120;
    public int TemporaryIpApprovalTokenLifetimeMinutes { get; set; } = 30;
    public int TemporaryIpApprovalEmailCooldownMinutes { get; set; } = 60;
    public int TemporaryIpApprovalMaxEmailsPerDay { get; set; } = 10;
}
