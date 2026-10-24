// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models;
using Microsoft.AspNetCore.Components;
namespace LinuxMadeSane.Web.Components.Ui;
public sealed record FileBrowserDocumentThumbnailContext(SftpItem Item, int SizePixels, RenderFragment Fallback);
