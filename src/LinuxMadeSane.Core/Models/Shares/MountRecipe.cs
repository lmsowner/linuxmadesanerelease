using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record MountRecipe(
    MountTargetType TargetType,
    string Title,
    string CommandOrEntry,
    string PlainEnglishExplanation,
    IReadOnlyList<string> OptionNotes);
