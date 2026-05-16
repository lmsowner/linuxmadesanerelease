// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed class CloudflareApiException : InvalidOperationException
{
    public CloudflareApiException(
        int statusCode,
        IReadOnlyList<CloudflareApiError> errors,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? BuildMessage(errors), innerException)
    {
        StatusCode = statusCode;
        Errors = errors;
    }

    public int StatusCode { get; }

    public IReadOnlyList<CloudflareApiError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<CloudflareApiError> errors)
    {
        if (errors.Count == 0)
        {
            return "Cloudflare rejected the request.";
        }

        return string.Join(" ", errors.Select(error => $"[{error.Code}] {error.Message}".Trim()));
    }
}

public sealed record CloudflareApiError(
    int Code,
    string Message,
    string? DocumentationUrl,
    string? Pointer);
