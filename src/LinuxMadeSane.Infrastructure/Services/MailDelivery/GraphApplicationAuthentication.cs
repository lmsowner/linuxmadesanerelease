// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Messaging;

namespace LinuxMadeSane.Infrastructure.Services;

// Shared by notifications and the SMTP relay. Keep the existing application-credential flow.
public static class GraphApplicationAuthentication
{
    public static async Task<(string? Token, int ExpiresIn)> AcquireAsync(HttpClient client, ISecretStore secrets,
        MessagingEmailSettings settings, CancellationToken token, string? certificateReference = null)
    {
        var endpoint = $"{settings.GraphAuthority.TrimEnd('/')}/{Uri.EscapeDataString(settings.GraphTenantId.Trim())}/oauth2/v2.0/token";
        var form = new Dictionary<string, string> {
            ["client_id"] = settings.GraphClientId.Trim(),
            ["scope"] = new Uri(settings.GraphBaseUrl).GetLeftPart(UriPartial.Authority) + "/.default", ["grant_type"] = "client_credentials" };
        if (!string.IsNullOrWhiteSpace(certificateReference))
        {
            var encoded = await secrets.ResolveSecretAsync(certificateReference, token);
            if (encoded is null) throw new MailProviderAuthenticationException("Microsoft application certificate is missing. Run guided registration again.");
            using var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(encoded), null);
            if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw new MailProviderAuthenticationException("The Microsoft application certificate expired. Prepare guided registration again and register the new public certificate.");
            using var key = certificate.GetRSAPrivateKey() ?? throw new MailProviderAuthenticationException("Microsoft application certificate has no signing key.");
            static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = DateTimeOffset.UtcNow;
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "PS256", ["typ"] = "JWT", ["x5t#S256"] = B64(SHA256.HashData(certificate.RawData)) }));
            var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { aud = endpoint, iss = settings.GraphClientId, sub = settings.GraphClientId, jti = Guid.NewGuid().ToString(), nbf = now.AddSeconds(-30).ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds() }));
            var unsigned = header + "." + payload;
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = unsigned + "." + B64(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
        else
        {
            var secret = await secrets.ResolveSecretAsync(settings.GraphClientSecretReference!, token);
            if (string.IsNullOrWhiteSpace(secret)) throw new MailProviderAuthenticationException("Microsoft application credential is missing. Reconnect Microsoft 365.");
            form["client_secret"] = secret;
        }
        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(form), token);
        if (!response.IsSuccessStatusCode)
        {
            var temporary = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            throw new MailProviderAuthenticationException(temporary ? "Microsoft sign-in is temporarily unavailable. The queue will retry." : "Microsoft application sign-in was rejected. Check tenant, application credentials and administrator authorisation.") { Transient = temporary };
        }
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return (json.GetProperty("access_token").GetString(), json.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600);
    }
}
public sealed class MailProviderAuthenticationException(string message) : Exception(message) { public bool Transient { get; init; } }
