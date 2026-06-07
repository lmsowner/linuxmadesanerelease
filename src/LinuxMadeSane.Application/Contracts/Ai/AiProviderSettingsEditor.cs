// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiProviderSettingsEditor : IValidatableObject
{
    public string ProviderKey { get; set; } = string.Empty;

    public bool IsExistingProvider => !string.IsNullOrWhiteSpace(ProviderKey);

    [Required]
    public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAi;

    [Required]
    [StringLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool IsDefault { get; set; }

    [StringLength(128)]
    public string DefaultModelId { get; set; } = string.Empty;

    [StringLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    public bool StreamingEnabled { get; set; } = true;

    public bool ToolUseEnabled { get; set; } = true;

    public bool RequiresApiKey { get; set; } = true;

    public bool HasApiKeyConfigured { get; set; }

    [StringLength(4096)]
    public string ApiKeyInput { get; set; } = string.Empty;

    public bool ClearStoredApiKey { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProviderType == AiProviderType.Unknown)
        {
            yield return new ValidationResult(
                "Select a supported provider type.",
                [nameof(ProviderType)]);
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            yield return new ValidationResult(
                "Display name is required.",
                [nameof(DisplayName)]);
        }

        if (string.IsNullOrWhiteSpace(DefaultModelId))
        {
            yield return new ValidationResult(
                "Default model is required.",
                [nameof(DefaultModelId)]);
        }

        if (ProviderType == AiProviderType.Custom)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                yield return new ValidationResult(
                    "Base URL is required for OpenAI-compatible providers.",
                    [nameof(BaseUrl)]);
            }
            else if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
                     uri.Scheme is not ("http" or "https"))
            {
                yield return new ValidationResult(
                    "Base URL must be an HTTP or HTTPS URL.",
                    [nameof(BaseUrl)]);
            }
        }

        if (IsDefault && !IsEnabled)
        {
            yield return new ValidationResult(
                "A default provider must also be enabled.",
                [nameof(IsDefault), nameof(IsEnabled)]);
        }

        if (RequiresApiKey &&
            (IsEnabled || IsDefault) &&
            string.IsNullOrWhiteSpace(ApiKeyInput) &&
            (!HasApiKeyConfigured || ClearStoredApiKey))
        {
            yield return new ValidationResult(
                "An API key is required for enabled or default providers.",
                [nameof(ApiKeyInput), nameof(ClearStoredApiKey)]);
        }

        if (ClearStoredApiKey && !string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            yield return new ValidationResult(
                "Clear the existing API key or enter a replacement value, but not both.",
                [nameof(ApiKeyInput), nameof(ClearStoredApiKey)]);
        }
    }
}
