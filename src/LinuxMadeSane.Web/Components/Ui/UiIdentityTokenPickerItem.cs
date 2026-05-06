namespace LinuxMadeSane.Web.Components.Ui;

public sealed record UiIdentityTokenPickerItem(
    string Key,
    string Title,
    string? Subtitle = null,
    string? SearchText = null);
