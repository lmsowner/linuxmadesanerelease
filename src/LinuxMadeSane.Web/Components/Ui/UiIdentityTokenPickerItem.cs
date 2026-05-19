// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Components.Ui;

public sealed record UiIdentityTokenPickerItem(
    string Key,
    string Title,
    string? Subtitle = null,
    string? SearchText = null);
