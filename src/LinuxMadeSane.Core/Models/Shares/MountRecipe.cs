// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record MountRecipe(
    MountTargetType TargetType,
    string Title,
    string CommandOrEntry,
    string PlainEnglishExplanation,
    IReadOnlyList<string> OptionNotes);
