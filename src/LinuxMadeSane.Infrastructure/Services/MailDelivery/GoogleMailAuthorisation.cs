// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Infrastructure.Services;

// Single-use, short-lived consent tickets. No refresh token is exposed to the browser.
public sealed class GoogleMailAuthorisation(IServiceScopeFactory scopes, IHttpClientFactory clients)
{
    private sealed record Ticket(string Owner, MailRelayApiSettings Settings, string Redirect, string Verifier, DateTimeOffset Expires)
    {
        public int Completing;
        public MailRelayApiSettings? Connected;
        public string? Error;
    }
    private readonly ConcurrentDictionary<string, Ticket> tickets = new();
    public (string State, string Url) Begin(string owner, MailRelayApiSettings settings, string redirect)
    {
        foreach (var pair in tickets.Where(p => p.Value.Expires < DateTimeOffset.UtcNow)) tickets.TryRemove(pair.Key, out _);
        if (tickets.Count >= 100) throw new InvalidOperationException("Too many pending sign-ins. Wait for old sign-ins to expire.");
        var uri = new Uri(redirect);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new InvalidOperationException("Google requires an HTTPS callback, or a localhost callback reached through an SSH browser tunnel.");
        var state = Random(); var verifier = Random();
        tickets[state] = new(owner, settings, redirect, verifier, DateTimeOffset.UtcNow.AddMinutes(15));
        var query = new Dictionary<string, string> {
            ["client_id"] = settings.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = HttpsOutboundMailProvider.GmailScope, ["access_type"] = "offline", ["prompt"] = "consent",
            ["state"] = state, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
        return (state, "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    }
    public (MailRelayApiSettings? Settings, string? Error) Read(string state, string owner)
    {
        if (!tickets.TryGetValue(state, out var ticket) || ticket.Owner != owner || ticket.Expires < DateTimeOffset.UtcNow)
            return (null, "Sign-in expired. Connect Google Account again.");
        return (ticket.Connected, ticket.Error);
    }
    public async Task<bool> CompleteAsync(string state, string code, CancellationToken token)
    {
        if (!tickets.TryGetValue(state, out var ticket) || ticket.Expires < DateTimeOffset.UtcNow || Interlocked.Exchange(ref ticket.Completing, 1) != 0) return false;
        try
        {
            using var scope = scopes.CreateScope(); var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var secret = await secrets.ResolveSecretAsync(ticket.Settings.ClientSecretReference!, token);
            using var client = clients.CreateClient(nameof(HttpsOutboundMailProvider));
            using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"] = ticket.Settings.ClientId, ["client_secret"] = secret ?? "", ["code"] = code,
                ["redirect_uri"] = ticket.Redirect, ["code_verifier"] = ticket.Verifier, ["grant_type"] = "authorization_code" }), token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException();
            var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(token));
            if (!json.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrWhiteSpace(refresh.GetString()) ||
                !json.TryGetProperty("scope", out var granted) || !granted.GetString()!.Split(' ').Contains(HttpsOutboundMailProvider.GmailScope)) throw new InvalidOperationException();
            var reference = await secrets.StoreSecretAsync(refresh.GetString()!, "Mail Relay Google renewable authorisation", token);
            ticket.Connected = ticket.Settings with { RefreshTokenReference = reference };
            await scope.ServiceProvider.GetRequiredService<MailProviderSetupDraftStore>().SaveAsync(new() { Mode = MailRelayOutboundMode.Gmail, Api = ticket.Connected }, token);
            return true;
        }
        catch (Exception) { ticket.Error = "Google consent could not be completed. Check the registered callback and consent policy, then connect again."; return false; }
    }
    public MailRelayApiSettings? GetConnected(string owner, string state)
    {
        if (!tickets.TryGetValue(state, out var ticket) || ticket.Owner != owner || ticket.Expires < DateTimeOffset.UtcNow) return null;
        if (ticket.Error is not null) throw new InvalidOperationException(ticket.Error);
        return ticket.Connected;
    }
    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
