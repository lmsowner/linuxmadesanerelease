// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SimpleEmailV2.Model;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record SesMailDnsRecord(string Name, string Type, string Value);
public sealed record SesMailSetupResult(string AccountId, bool Sandbox, bool Verified, IReadOnlyList<SesMailDnsRecord> DnsRecords, string SendingPolicy);

public sealed partial class HttpsOutboundMailProvider
{
    // Use the same SDK authentication as sending; no AWS CLI or PowerShell prerequisite.
    public async Task<SesMailSetupResult> PrepareSesAsync(MailRelayOutboundSettings settings, CancellationToken token = default)
    {
        if (settings.Mode != MailRelayOutboundMode.AmazonSes || Validate(settings) is { })
            throw new InvalidOperationException(Validate(settings) ?? "Choose Amazon SES first.");
        var requestedCancellation = token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2)); token = timeout.Token;
        try
        {
            using var sts = await CreateStsAsync(settings.Api, token);
            var caller = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), token);
            if (caller.Account?.Length != 12 || caller.Account.Any(c => !char.IsAsciiDigit(c)) || caller.Arn?.Split(':') is not { Length: >= 6 } arn)
                throw new InvalidOperationException("AWS did not confirm the account identity. Reconnect the AWS account and retry.");
            using var ses = await CreateSesAsync(settings.Api, token);
            GetEmailIdentityResponse identity;
            try { identity = await ses.GetEmailIdentityAsync(new() { EmailIdentity = settings.Api.SendingIdentity }, token); }
            catch (NotFoundException)
            {
                try { await ses.CreateEmailIdentityAsync(new() { EmailIdentity = settings.Api.SendingIdentity }, token); }
                catch (AlreadyExistsException) { /* Another setup completed creation; read the identity below. */ }
                identity = await ses.GetEmailIdentityAsync(new() { EmailIdentity = settings.Api.SendingIdentity }, token);
            }
            var account = await ses.GetAccountAsync(new GetAccountRequest(), token);
            var records = new List<SesMailDnsRecord>();
            if (!settings.Api.SendingIdentity.Contains('@') && identity.DkimAttributes?.Tokens is { Count: > 0 } tokens)
            {
                var zone = identity.DkimAttributes.SigningHostedZone;
                if (string.IsNullOrWhiteSpace(zone)) throw new InvalidOperationException("SES has not returned its DKIM signing zone yet. Retry preparation shortly; no DNS records were changed.");
                foreach (var value in tokens) records.Add(new($"{value}._domainkey.{settings.Api.SendingIdentity}", "CNAME", $"{value}.{zone}"));
            }
            var resource = $"arn:{arn[1]}:ses:{settings.Api.Region}:{caller.Account}:identity/{settings.Api.SendingIdentity}";
            var policy = JsonSerializer.Serialize(new { Version = "2012-10-17", Statement = new object[] {
                new { Effect = "Allow", Action = new[] { "ses:SendEmail" }, Resource = resource, Condition = new { StringEquals = new Dictionary<string,string> { ["ses:FromAddress"] = settings.Api.SenderAddress } } },
                new { Effect = "Allow", Action = new[] { "ses:GetEmailIdentity" }, Resource = resource },
                new { Effect = "Allow", Action = new[] { "ses:GetAccount" }, Resource = "*" }
            } }, new JsonSerializerOptions { WriteIndented = true });
            return new(caller.Account, account.ProductionAccessEnabled != true, identity.VerifiedForSendingStatus == true, records, policy);
        }
        catch (OperationCanceledException) when (!requestedCancellation.IsCancellationRequested)
        { throw new InvalidOperationException("AWS identity preparation timed out. Retry here; any identity already created will be reused."); }
        catch (AmazonServiceException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        { throw new InvalidOperationException("AWS denied identity preparation. Use an AWS administrator or role allowed to manage this SES identity, then retry here. Live mail was not changed."); }
        catch (AmazonClientException)
        { throw new InvalidOperationException("AWS sign-in or connectivity could not be confirmed. Select the host's AWS role or enter your AWS account credentials, then retry here. No additional software is required."); }
    }
    private async Task<IAmazonSecurityTokenService> CreateStsAsync(MailRelayApiSettings settings, CancellationToken token)
    {
        if (stsFactory is not null) return await stsFactory(settings, token);
        var config = new AmazonSecurityTokenServiceConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region), MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(45) };
        var credentials = await AwsCredentialsAsync(settings, token);
        return credentials is null ? new AmazonSecurityTokenServiceClient(config) : new AmazonSecurityTokenServiceClient(credentials, config);
    }
}
