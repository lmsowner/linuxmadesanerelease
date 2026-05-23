// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopTrayHostedService(
    DesktopAssistantLaunchTicketCache launchTicketCache,
    DesktopAssistantNativeMessageBus nativeMessageBus,
    IOptions<DesktopSessionHelperOptions> options,
    ILogger<DesktopTrayHostedService> logger) : IHostedService
{
    private Task? desktopTask;
    private CancellationTokenSource? desktopCancellation;
    private DesktopAssistantAvaloniaContext? avaloniaContext;

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
            logger.LogDebug("LMS desktop helper desktop UI is unavailable because no graphical display is present.");
            return Task.CompletedTask;
        }

        if (!TryResolveLocalLmsUri(out var localLmsUri))
        {
            logger.LogWarning("LMS desktop helper desktop UI is disabled because the configured LMS URL is not a local HTTP URL.");
            return Task.CompletedTask;
        }

        desktopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        avaloniaContext = new DesktopAssistantAvaloniaContext(
            launchTicketCache,
            nativeMessageBus,
            options.Value,
            localLmsUri,
            logger);
        logger.LogInformation(
            "LMS desktop helper starting native desktop UI. DISPLAY={Display}, WAYLAND_DISPLAY={WaylandDisplay}, desktop={CurrentDesktop}, trayIcon={TrayIconPath}, openWindowOnStart={OpenWindowOnStart}.",
            Environment.GetEnvironmentVariable("DISPLAY") ?? "none",
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "none",
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ??
            Environment.GetEnvironmentVariable("DESKTOP_SESSION") ??
            "none",
            avaloniaContext.TrayIconPath,
            avaloniaContext.OpenWindowOnStart);
        desktopTask = Task.Run(() => RunDesktopAsync(avaloniaContext, desktopCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        desktopCancellation?.Cancel();
        avaloniaContext?.Shutdown();

        if (desktopTask is not null)
        {
            await Task.WhenAny(desktopTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        }
    }

    private Task RunDesktopAsync(
        DesktopAssistantAvaloniaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            SuppressAccessibilityBridgeWarnings();
            using var _ = cancellationToken.Register(context.Shutdown);
            AppBuilder.Configure(() => new DesktopAssistantAvaloniaApp(context))
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(
                    [],
                    lifetime =>
                    {
                        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        context.SetLifetime(lifetime);
                    });
            logger.LogInformation("LMS desktop helper native desktop UI stopped.");
        }
        catch (Exception exception) when (IsDesktopUiUnavailable(exception))
        {
            logger.LogDebug(exception, "LMS desktop helper native desktop UI could not start.");
        }

        return Task.CompletedTask;
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

    private static bool HasDisplay() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static void SuppressAccessibilityBridgeWarnings()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_AT_BRIDGE")))
        {
            Environment.SetEnvironmentVariable("NO_AT_BRIDGE", "1");
        }
    }

    private static bool IsDesktopUiUnavailable(Exception exception) =>
        exception is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException ||
        exception.InnerException is not null && IsDesktopUiUnavailable(exception.InnerException);
}

internal sealed class DesktopAssistantAvaloniaContext(
    DesktopAssistantLaunchTicketCache launchTicketCache,
    DesktopAssistantNativeMessageBus nativeMessageBus,
    DesktopSessionHelperOptions options,
    Uri localLmsUri,
    ILogger logger)
{
    private IClassicDesktopStyleApplicationLifetime? lifetime;
    private DesktopAssistantWindow? assistantWindow;

    public DesktopAssistantNativeApiClient ApiClient { get; } = new(launchTicketCache, localLmsUri);

    public DesktopAssistantNativeMessageBus NativeMessageBus => nativeMessageBus;

    public string TrayIconPath => ResolveTrayIconPath(options);

    public bool OpenWindowOnStart => ResolveOpenWindowOnStart(options);

    public WindowIcon? WindowIcon =>
        File.Exists(TrayIconPath)
            ? new WindowIcon(TrayIconPath)
            : null;

    public void SetLifetime(IClassicDesktopStyleApplicationLifetime applicationLifetime) =>
        lifetime = applicationLifetime;

    public void RegisterWindow(DesktopAssistantWindow window) => assistantWindow = window;

    public void ShowAssistantWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (assistantWindow is null)
            {
                return;
            }

            assistantWindow.Show();
            assistantWindow.Activate();
        });
    }

    public void OpenLocalLms(string returnUrl)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { BuildLaunchUri(returnUrl).ToString() }
            };
            startInfo.Environment["NO_AT_BRIDGE"] = "1";
            using var process = System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not open Linux Made Sane in the default browser.");
        }
    }

    public void LogDesktopUiStarted(bool trayRegistered)
    {
        logger.LogInformation(
            "LMS desktop helper native desktop UI started. trayRegistered={TrayRegistered}, openWindowOnStart={OpenWindowOnStart}.",
            trayRegistered,
            OpenWindowOnStart);
    }

    public void LogTrayRegistrationFailed(Exception exception)
    {
        logger.LogWarning(exception, "LMS desktop helper could not register the tray icon. The assistant window can still open directly.");
    }

    public void Shutdown()
    {
        Dispatcher.UIThread.Post(() =>
        {
            assistantWindow?.Hide();
            lifetime?.TryShutdown();
        });
    }

    private Uri BuildLaunchUri(string returnUrl)
    {
        var token = launchTicketCache.TryPeekToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            var snapshot = launchTicketCache.GetDebugSnapshot();
            logger.LogWarning(
                "Opening LMS without a desktop launch ticket. Cache: hasTicket={HasTicket}, valid={HasValidTicket}, token={TokenPreview}, expires={ExpiresAtUtc}, updated={LastUpdatedAtUtc}, waiter={WaiterActive}.",
                snapshot.HasTicket,
                snapshot.HasValidTicket,
                snapshot.TokenPreview,
                snapshot.ExpiresAtUtc,
                snapshot.LastUpdatedAtUtc,
                snapshot.WaiterActive);
            return BuildLocalUri(returnUrl);
        }

        logger.LogInformation(
            "Opening LMS with desktop launch ticket {TokenPreview}.",
            DesktopAssistantLaunchTicketCache.PreviewToken(token));
        var launchBuilder = new UriBuilder(localLmsUri)
        {
            Path = "/desktop-assistant/launch",
            Query = $"ticket={Uri.EscapeDataString(token)}"
        };
        return launchBuilder.Uri;
    }

    private Uri BuildLocalUri(string returnUrl)
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

    private static string ResolveTrayIconPath(DesktopSessionHelperOptions options)
    {
        var environmentValue = Environment.GetEnvironmentVariable("LMS_DESKTOP_HELPER_TRAY_ICON");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            var resolvedEnvironmentPath = TryResolveIconPath(environmentValue.Trim());
            if (!string.IsNullOrWhiteSpace(resolvedEnvironmentPath))
            {
                return resolvedEnvironmentPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.TrayIconPath))
        {
            var resolvedOptionPath = TryResolveIconPath(options.TrayIconPath.Trim());
            if (!string.IsNullOrWhiteSpace(resolvedOptionPath))
            {
                return resolvedOptionPath;
            }
        }

        return TryResolveIconPath("lms-logo-192.png") ?? string.Empty;
    }

    private static bool ResolveOpenWindowOnStart(DesktopSessionHelperOptions options)
    {
        var environmentValue = Environment.GetEnvironmentVariable("LMS_DESKTOP_HELPER_OPEN_WINDOW");
        return bool.TryParse(environmentValue, out var enabled)
            ? enabled
            : options.OpenWindowOnStart;
    }

    private static string? TryResolveIconPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        foreach (var basePath in GetIconSearchRoots())
        {
            var candidate = Path.GetFullPath(Path.Combine(basePath, path));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetIconSearchRoots()
    {
        yield return Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory;
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LinuxMadeSane.Web", "wwwroot", "images"));
    }
}
