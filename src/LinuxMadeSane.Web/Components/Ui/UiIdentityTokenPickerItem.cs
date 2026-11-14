// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Web.Components.Ui;

public sealed record UiIdentityTokenPickerItem(
    string Key,
    string Title,
    string? Subtitle = null,
    string? SearchText = null);
