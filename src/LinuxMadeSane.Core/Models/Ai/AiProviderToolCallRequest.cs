// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderToolCallRequest(
    string ProviderToolCallId,
    string ToolName,
    string ArgumentsJson);
