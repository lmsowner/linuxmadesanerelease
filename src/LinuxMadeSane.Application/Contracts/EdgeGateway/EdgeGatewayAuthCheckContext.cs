using System.Net;
using System.Security.Claims;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayAuthCheckContext(
    string ForwardedHost,
    string ForwardedProto,
    string ForwardedUri,
    string ForwardedFor,
    string Host,
    IPAddress? RemoteIpAddress,
    ClaimsPrincipal User);
