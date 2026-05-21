// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;
using Gtk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.DesktopHelper;

#pragma warning disable CS0612, CS0618 // GTK status icons are deprecated, but XFCE still exposes this tray surface.

public sealed class DesktopTrayHostedService(
    DesktopAssistantLaunchTicketCache launchTicketCache,
    IOptions<DesktopSessionHelperOptions> options,
    ILogger<DesktopTrayHostedService> logger) : IHostedService
{
    private Task? trayTask;
    private CancellationTokenSource? trayCancellation;
    private StatusIcon? statusIcon;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Task.CompletedTask;
        }

        if (!options.Value.TrayEnabled)
        {
            logger.LogDebug("LMS desktop helper tray icon is disabled.");
            return Task.CompletedTask;
        }

        if (!HasDisplay())
        {
            logger.LogDebug("LMS desktop helper tray icon is unavailable because no graphical display is present.");
            return Task.CompletedTask;
        }

        if (!TryResolveLocalLmsUri(out var localLmsUri))
        {
            logger.LogWarning("LMS desktop helper tray icon is disabled because the configured LMS URL is not a local HTTP URL.");
            return Task.CompletedTask;
        }

        trayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        trayTask = Task.Run(() => RunTrayAsync(localLmsUri, trayCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        trayCancellation?.Cancel();
        RequestGtkQuit();

        if (trayTask is not null)
        {
            await Task.WhenAny(trayTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        }
    }

    private Task RunTrayAsync(Uri localLmsUri, CancellationToken cancellationToken)
    {
        try
        {
            Gtk.Application.Init();

            statusIcon = CreateStatusIcon();
            statusIcon.TooltipText = "Linux Made Sane Desktop Assistant";
            statusIcon.Visible = true;
            statusIcon.Activate += (_, _) => OpenLocalLms(BuildLaunchUri(localLmsUri, "/desktop-assistant?fromTray=1"));
            statusIcon.PopupMenu += (_, _) => ShowMenu(localLmsUri);

            using var _ = cancellationToken.Register(RequestGtkQuit);
            logger.LogInformation("LMS desktop helper tray icon is running.");
            Gtk.Application.Run();
        }
        catch (Exception exception) when (IsTrayUnavailable(exception))
        {
            logger.LogDebug(exception, "LMS desktop helper tray icon could not start.");
        }
        finally
        {
            statusIcon = null;
        }

        return Task.CompletedTask;
    }

    private StatusIcon CreateStatusIcon()
    {
        var iconPath = ResolveTrayIconPath();
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            return new StatusIcon
            {
                FromFile = iconPath
            };
        }

        var iconName = ResolveTrayIconName();
        return StatusIcon.NewFromIconName(iconName);
    }

    private void ShowMenu(Uri localLmsUri)
    {
        var menu = new Menu();

        var openItem = new MenuItem("Open Linux Made Sane");
        openItem.Activated += (_, _) => OpenLocalLms(BuildLocalUri(localLmsUri, "/"));
        menu.Append(openItem);

        var assistantItem = new MenuItem("Ask Desktop Assistant");
        assistantItem.Activated += (_, _) => OpenLocalLms(BuildLaunchUri(localLmsUri, "/desktop-assistant?fromTray=1"));
        menu.Append(assistantItem);

        menu.Append(new SeparatorMenuItem());

        var hideItem = new MenuItem("Hide tray icon");
        hideItem.Activated += (_, _) =>
        {
            if (statusIcon is not null)
            {
                statusIcon.Visible = false;
            }
        };
        menu.Append(hideItem);

        menu.ShowAll();
        menu.Popup();
    }

    private Uri BuildLaunchUri(Uri localLmsUri, string returnUrl)
    {
        var token = launchTicketCache.TryTakeToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildLocalUri(localLmsUri, returnUrl);
        }

        var launchBuilder = new UriBuilder(localLmsUri)
        {
            Path = "/desktop-assistant/launch",
            Query = $"ticket={Uri.EscapeDataString(token)}"
        };
        return launchBuilder.Uri;
    }

    private static Uri BuildLocalUri(Uri localLmsUri, string returnUrl)
    {
        var normalizedReturnUrl = NormalizeReturnUrl(returnUrl);
        var separatorIndex = normalizedReturnUrl.IndexOf('?', StringComparison.Ordinal);
        var path = separatorIndex < 0
            ? normalizedReturnUrl
            : normalizedReturnUrl[..separatorIndex];
        var query = separatorIndex < 0
            ? string.Empty
            : normalizedReturnUrl[(separatorIndex + 1)..];

        var builder = new UriBuilder(localLmsUri)
        {
            Path = path,
            Query = query
        };
        return builder.Uri;
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var trimmed = returnUrl.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal) &&
               !trimmed.StartsWith("//", StringComparison.Ordinal) &&
               !trimmed.StartsWith("/\\", StringComparison.Ordinal)
            ? trimmed
            : "/";
    }

    private void OpenLocalLms(Uri localLmsUri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { localLmsUri.ToString() }
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not open Linux Made Sane in the default browser.");
        }
    }

    private void RequestGtkQuit()
    {
        try
        {
            Gtk.Application.Invoke(delegate
            {
                if (statusIcon is not null)
                {
                    statusIcon.Visible = false;
                    statusIcon.Dispose();
                }

                Gtk.Application.Quit();
            });
        }
        catch (Exception exception) when (IsTrayUnavailable(exception))
        {
            logger.LogDebug(exception, "LMS desktop helper tray icon was already stopped.");
        }
    }

    private bool TryResolveLocalLmsUri(out Uri localLmsUri)
    {
        var configuredValue = Environment.GetEnvironmentVariable("LMS_DESKTOP_HELPER_LMS_URL");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            configuredValue = options.Value.LocalLmsUrl;
        }

        if (Uri.TryCreate(configuredValue.Trim(), UriKind.Absolute, out localLmsUri!) &&
            (string.Equals(localLmsUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(localLmsUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            localLmsUri.IsLoopback)
        {
            return true;
        }

        localLmsUri = new Uri("http://127.0.0.1:5080/desktop-assistant");
        return false;
    }

    private string? ResolveTrayIconPath()
    {
        var environmentValue = Environment.GetEnvironmentVariable("LMS_DESKTOP_HELPER_TRAY_ICON");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.Value.TrayIconPath))
        {
            return options.Value.TrayIconPath.Trim();
        }

        var publishedIcon = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "wwwroot",
            "images",
            "lms-logo-192.png"));

        return publishedIcon;
    }

    private string ResolveTrayIconName()
    {
        var environmentValue = Environment.GetEnvironmentVariable("LMS_DESKTOP_HELPER_TRAY_ICON_NAME");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        return string.IsNullOrWhiteSpace(options.Value.TrayIconName)
            ? "applications-system"
            : options.Value.TrayIconName.Trim();
    }

    private static bool HasDisplay() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static bool IsTrayUnavailable(Exception exception) =>
        exception is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException ||
        exception is GLib.GException ||
        exception.InnerException is not null && IsTrayUnavailable(exception.InnerException);
}

#pragma warning restore CS0612, CS0618
