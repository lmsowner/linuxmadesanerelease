// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public static class DesktopSessionBrokerMessageTypes
{
    public const string Hello = "hello";
    public const string Heartbeat = "heartbeat";
    public const string ActionRequest = "actionRequest";
    public const string ActionResult = "actionResult";
    public const string LaunchTicket = "launchTicket";
}

public sealed record DesktopSessionBrokerMessage(
    string MessageType,
    DesktopSessionCapabilityReport? CapabilityReport,
    DateTimeOffset SentAtUtc)
{
    public DesktopSessionActionRequest? ActionRequest { get; init; }

    public DesktopSessionActionResult? ActionResult { get; init; }

    public DesktopAssistantLaunchTicket? LaunchTicket { get; init; }
}
