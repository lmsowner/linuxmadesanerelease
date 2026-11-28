// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Reflection;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;
public sealed class MailRelaySesSetupTests
{
    [Fact]
    public async Task MissingIdentityIsPreparedWithBundledSdkAndExactProviderDns()
    {
        var ses = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, Ses>(); var state = (Ses)(object)ses;
        var sts = DispatchProxy.Create<IAmazonSecurityTokenService, Sts>();
        var provider = Create(ses, sts);
        var result = await provider.PrepareSesAsync(Settings());
        Assert.Equal("123456789012", result.AccountId); Assert.True(result.Sandbox); Assert.False(result.Verified);
        Assert.Equal(1, state.Created); Assert.Equal(2, state.Reads);
        Assert.Equal(new SesMailDnsRecord("token._domainkey.example.test", "CNAME", "token.signing.example.test"), Assert.Single(result.DnsRecords));
        Assert.Contains("arn:aws:ses:eu-west-1:123456789012:identity/example.test", result.SendingPolicy);
        Assert.Contains("lms@example.test", result.SendingPolicy); Assert.DoesNotContain("ses:*", result.SendingPolicy);
    }
    [Fact]
    public async Task ExistingVerifiedIdentityIsReadWithoutCreatingOrSendingAnything()
    {
        var ses = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, Ses>(); var state = (Ses)(object)ses; state.Missing = false; state.Verified = true;
        var result = await Create(ses, DispatchProxy.Create<IAmazonSecurityTokenService, Sts>()).PrepareSesAsync(Settings());
        Assert.True(result.Verified); Assert.Equal(0, state.Created); Assert.Equal(1, state.Reads);
    }
    [Fact]
    public async Task AccountDenialIsReportedWithoutLeakingSdkErrorOrCreatingIdentity()
    {
        var ses = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, Ses>(); var sts = DispatchProxy.Create<IAmazonSecurityTokenService, Sts>(); ((Sts)(object)sts).Denied = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(ses, sts).PrepareSesAsync(Settings()));
        Assert.Contains("AWS denied", error.Message); Assert.DoesNotContain("raw SDK detail", error.Message); Assert.Equal(0, ((Ses)(object)ses).Reads);
    }
    private static MailRelayOutboundSettings Settings() => new() { Mode = MailRelayOutboundMode.AmazonSes, Api = new() { SenderAddress = "lms@example.test", SendingIdentity = "example.test", Region = "eu-west-1" } };
    private static HttpsOutboundMailProvider Create(IAmazonSimpleEmailServiceV2 ses, IAmazonSecurityTokenService sts) => new(DispatchProxy.Create<ISecretStore, Reject>(), new NoHttp(), (_, _) => Task.FromResult(ses), (_, _) => Task.FromResult(sts));
    public class Sts : DispatchProxy
    {
        public bool Denied;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "Dispose") return null;
            if (Denied) throw new AmazonSecurityTokenServiceException("raw SDK detail") { StatusCode = HttpStatusCode.Forbidden };
            return Task.FromResult(new GetCallerIdentityResponse { Account = "123456789012", Arn = "arn:aws:iam::123456789012:role/LMS" });
        }
    }
    public class Ses : DispatchProxy
    {
        public bool Missing = true, Verified;
        public int Reads, Created;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "Dispose": return null;
                case "GetEmailIdentityAsync":
                    Reads++; if (Missing && Reads == 1) throw new NotFoundException("fixture");
                    return Task.FromResult(new GetEmailIdentityResponse { VerifiedForSendingStatus = Verified, DkimAttributes = new() { Tokens = ["token"], SigningHostedZone = "signing.example.test" } });
                case "CreateEmailIdentityAsync": Created++; return Task.FromResult(new CreateEmailIdentityResponse());
                case "GetAccountAsync": return Task.FromResult(new GetAccountResponse { ProductionAccessEnabled = false, SendingEnabled = true });
                default: throw new InvalidOperationException("No message submission or unrelated AWS call is allowed");
            }
        }
    }
    public class Reject : DispatchProxy { protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("No secret access expected"); }
    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("SDK clients are mocked"); }
}
