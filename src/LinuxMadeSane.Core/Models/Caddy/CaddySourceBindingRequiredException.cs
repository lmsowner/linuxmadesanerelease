// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Caddy;

public sealed class CaddySourceBindingRequiredException() : InvalidOperationException(
    "The installed Caddy cannot bind the selected source address. Use Upgrade Caddy below, then test again. " +
    "Current behaviour remains available; no live configuration was changed.");
