// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

internal sealed class DesktopAssistantNativeApiClient(
    DesktopAssistantLaunchTicketCache launchTicketCache,
    Uri localLmsUri)
{
    private static readonly TimeSpan TicketWaitTimeout = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient = new();

    public Task<DesktopAssistantNativeWorkspaceResponse> GetWorkspaceAsync(
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var query = sessionId.HasValue
            ? $"sessionId={Uri.EscapeDataString(sessionId.Value.ToString("D"))}"
            : string.Empty;
        return SendAsync<DesktopAssistantNativeWorkspaceResponse>(
            HttpMethod.Get,
            BuildUri("/api/desktop-assistant/native/workspace", query),
            null,
            cancellationToken);
    }

    public Task<DesktopAssistantNativeWorkspaceResponse> CreateSessionAsync(
        string? providerKey,
        string? modelId,
        CancellationToken cancellationToken = default) =>
        SendAsync<DesktopAssistantNativeWorkspaceResponse>(
            HttpMethod.Post,
            BuildUri("/api/desktop-assistant/native/sessions"),
            new DesktopAssistantNativeCreateSessionRequest(providerKey, modelId),
            cancellationToken);

    public Task<DesktopAssistantNativeWorkspaceResponse> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        SendAsync<DesktopAssistantNativeWorkspaceResponse>(
            HttpMethod.Delete,
            BuildUri($"/api/desktop-assistant/native/sessions/{Uri.EscapeDataString(sessionId.ToString("D"))}"),
            null,
            cancellationToken);

    public Task<DesktopAssistantNativeWorkspaceResponse> SendMessageAsync(
        Guid? sessionId,
        string message,
        string? providerKey,
        string? modelId,
        CancellationToken cancellationToken = default) =>
        SendAsync<DesktopAssistantNativeWorkspaceResponse>(
            HttpMethod.Post,
            BuildUri("/api/desktop-assistant/native/messages"),
            new DesktopAssistantNativeSendMessageRequest(sessionId, message, providerKey, modelId),
            cancellationToken);

    public Task<DesktopAssistantNativeWorkspaceResponse> ApproveFixAsync(
        Guid? sessionId,
        DesktopAssistantNativeProposedFix fix,
        string? providerKey,
        string? modelId,
        CancellationToken cancellationToken = default) =>
        SendAsync<DesktopAssistantNativeWorkspaceResponse>(
            HttpMethod.Post,
            BuildUri("/api/desktop-assistant/native/fixes/approve"),
            new DesktopAssistantNativeApproveFixRequest(sessionId, fix, providerKey, modelId),
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        Uri uri,
        object? body,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ticket = await launchTicketCache.WaitForTokenAsync(TicketWaitTimeout, cancellationToken);
            if (string.IsNullOrWhiteSpace(ticket))
            {
                throw new InvalidOperationException($"LMS is still linking this desktop session. Ticket cache: {FormatTicketCacheSnapshot()}.");
            }

            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("X-LMS-Desktop-Ticket", ticket);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonOptions);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                launchTicketCache.Invalidate(ticket);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                    ? $"LMS returned HTTP {(int)response.StatusCode}."
                    : message);
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken) ??
                   throw new InvalidOperationException("LMS returned an empty Desktop Assistant response.");
        }

        throw new InvalidOperationException($"LMS could not validate this desktop session yet. Ticket cache: {FormatTicketCacheSnapshot()}.");
    }

    private Uri BuildUri(string path, string query = "")
    {
        var builder = new UriBuilder(localLmsUri)
        {
            Path = path,
            Query = query
        };
        return builder.Uri;
    }

    private string FormatTicketCacheSnapshot()
    {
        var snapshot = launchTicketCache.GetDebugSnapshot();
        return $"hasTicket={snapshot.HasTicket}, valid={snapshot.HasValidTicket}, token={snapshot.TokenPreview}, expires={snapshot.ExpiresAtUtc?.ToString("O") ?? "none"}, updated={snapshot.LastUpdatedAtUtc?.ToString("O") ?? "never"}, invalidated={snapshot.LastInvalidatedAtUtc?.ToString("O") ?? "never"}, waiter={snapshot.WaiterActive}, lms={localLmsUri}";
    }
}
