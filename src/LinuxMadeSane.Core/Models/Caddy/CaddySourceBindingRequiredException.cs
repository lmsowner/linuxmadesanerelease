// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Caddy;

public sealed class CaddySourceBindingRequiredException() : InvalidOperationException(
    "The installed Caddy cannot bind the selected source address. Use Upgrade Caddy below, then test again. " +
    "Current behaviour remains available; no live configuration was changed.");
