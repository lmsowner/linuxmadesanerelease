// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace LinuxMadeSane.DesktopHelper;

internal sealed class DesktopAssistantAvaloniaApp(DesktopAssistantAvaloniaContext context) : Application
{
    private TrayIcon? trayIcon;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            context.SetLifetime(desktop);

            var window = new DesktopAssistantWindow(context);
            context.RegisterWindow(window);

            var trayRegistered = false;
            try
            {
                trayIcon = CreateTrayIcon();
                TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
                trayRegistered = true;
            }
            catch (Exception exception)
            {
                context.LogTrayRegistrationFailed(exception);
            }

            context.LogDesktopUiStarted(trayRegistered);
            if (context.OpenWindowOnStart)
            {
                context.ShowAssistantWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon CreateTrayIcon()
    {
        var menu = new NativeMenu();
        var assistantItem = new NativeMenuItem("Desktop Assistant");
        assistantItem.Click += (_, _) => context.ShowAssistantWindow();
        menu.Add(assistantItem);

        var webItem = new NativeMenuItem("Open LMS");
        webItem.Click += (_, _) => context.OpenLocalLms("/");
        menu.Add(webItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Quit Desktop Helper");
        quitItem.Click += (_, _) => context.QuitApplication();
        menu.Add(quitItem);

        var icon = new TrayIcon
        {
            Icon = context.WindowIcon,
            ToolTipText = "Linux Made Sane Desktop Assistant",
            IsVisible = true,
            Menu = menu
        };
        icon.Clicked += (_, _) => context.ShowAssistantWindow();
        return icon;
    }
}
