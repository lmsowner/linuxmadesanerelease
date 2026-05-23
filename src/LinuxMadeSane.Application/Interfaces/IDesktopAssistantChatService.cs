// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.DesktopAssistant;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Application.Interfaces;

public interface IDesktopAssistantChatService
{
    Task<DesktopAssistantChatWorkspaceViewModel> GetWorkspaceAsync(
        Guid? sessionId,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        CancellationToken cancellationToken = default);

    Task<Guid> CreateSessionAsync(
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<DesktopAssistantChatWorkspaceViewModel> DeleteSessionAsync(
        Guid sessionId,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        CancellationToken cancellationToken = default);

    Task<DesktopAssistantChatWorkspaceViewModel> SendMessageAsync(
        Guid? sessionId,
        string message,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<DesktopAssistantChatWorkspaceViewModel> ApplyKeyboardLayoutAsync(
        Guid? sessionId,
        string layout,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<DesktopAssistantChatWorkspaceViewModel> InstallAptPackagesAsync(
        Guid? sessionId,
        IReadOnlyList<string> packageNames,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<DesktopAssistantChatWorkspaceViewModel> RepairAptSourcesAsync(
        Guid? sessionId,
        IReadOnlyDictionary<string, string> arguments,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);
}
