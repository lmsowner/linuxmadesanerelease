// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public static class EdgeGatewayAuthenticationPaths
{
    public const string Login = "/LMSMFALogin";
    public const string ApiPrefix = "/LMSMFAAuth";
    public const string LoginPost = $"{ApiPrefix}/login";
    public const string EmailSend = $"{ApiPrefix}/email/send";
    public const string EmailComplete = $"{ApiPrefix}/email/complete";
    public const string EmailLink = $"{ApiPrefix}/email/link";
    public const string PasskeyOptions = $"{ApiPrefix}/passkeys/options";
    public const string PasskeyComplete = $"{ApiPrefix}/passkeys/complete";
}
