// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Diagnostics;
using Microsoft.Playwright;

namespace LinuxMadeSane.Web.Services;

public sealed class ChromiumPageRenderer : IHttpServicePageRenderer
{
    public const string BrowserDirectory = "/var/cache/linuxmadesane-web-previews";
    public const string InstallCommand = "web-preview-browser-install";
    private static string DriverVersion => typeof(Playwright).Assembly.GetName().Version!.ToString();
    public static string? BrowserExecutable
    {
        get
        {
            try
            {
                if (File.ReadAllText(Path.Combine(BrowserDirectory, "driver-version.txt")).Trim() != DriverVersion) return null;
                var path = File.ReadAllText(Path.Combine(BrowserDirectory, "executable.txt")).Trim();
                return path.StartsWith(BrowserDirectory + "/", StringComparison.Ordinal) &&
                    Path.GetFullPath(path) == path && File.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public static int InstallBrowser()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Chromium preview installation requires Linux.");
        if (Directory.Exists(BrowserDirectory) && (File.GetAttributes(BrowserDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The preview browser directory must not be a symbolic link.");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", BrowserDirectory);
        var code = Microsoft.Playwright.Program.Main(["install", "--with-deps", "--only-shell", "chromium"]);
        if (code != 0) return code;
        var readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var executable = readable | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(BrowserDirectory, executable);
        foreach (var dir in Directory.EnumerateDirectories(BrowserDirectory, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) File.SetUnixFileMode(dir, executable);
        foreach (var file in Directory.EnumerateFiles(BrowserDirectory, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                File.SetUnixFileMode(file, (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0 ? executable : readable);
        var binary = Directory.EnumerateFiles(BrowserDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is "headless_shell" or "chrome-headless-shell")
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        // Ubuntu restricts user namespaces. Allow only this root-managed browser binary,
        // following Chromium's documented per-executable policy; never alter a global sysctl.
        if (File.Exists("/proc/sys/kernel/apparmor_restrict_unprivileged_userns") &&
            File.ReadAllText("/proc/sys/kernel/apparmor_restrict_unprivileged_userns").Trim() == "1")
        {
            const string profilePath = "/etc/apparmor.d/lms-web-preview";
            var profile = $"abi <abi/4.0>,\ninclude <tunables/global>\nprofile lms-web-preview \"{binary}\" flags=(unconfined) {{\n  userns,\n}}\n";
            var previous = File.Exists(profilePath) ? File.ReadAllText(profilePath) : null;
            File.WriteAllText(profilePath, profile);
            File.SetUnixFileMode(profilePath, readable);
            using var parser = Process.Start(new ProcessStartInfo("apparmor_parser") { ArgumentList = { "-r", profilePath }, UseShellExecute = false });
            parser!.WaitForExit();
            if (parser.ExitCode != 0)
            {
                if (previous is null) File.Delete(profilePath); else File.WriteAllText(profilePath, previous);
                return parser.ExitCode;
            }
        }
        File.WriteAllText(Path.Combine(BrowserDirectory, "executable.txt"), binary);
        File.SetUnixFileMode(Path.Combine(BrowserDirectory, "executable.txt"), readable);
        File.WriteAllText(Path.Combine(BrowserDirectory, "driver-version.txt"), DriverVersion);
        File.SetUnixFileMode(Path.Combine(BrowserDirectory, "driver-version.txt"), readable);
        return 0;
    }

    public async Task<byte[]> RenderAsync(Uri origin, CancellationToken ct)
    {
        var binary = BrowserExecutable ?? throw new InvalidOperationException("Install Chromium previews first.");
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo") &&
            long.TryParse((await File.ReadAllLinesAsync("/proc/meminfo", ct)).FirstOrDefault(x => x.StartsWith("MemAvailable:"))?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var kb) && kb < 512 * 1024)
            throw new InvalidOperationException("Insufficient memory to render a new homepage safely.");
        using var playwright = await Playwright.CreateAsync();
        ct.ThrowIfCancellationRequested();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            ExecutablePath = binary, Headless = true, ChromiumSandbox = true, Timeout = 8000,
            Args = ["--disable-dev-shm-usage", "--renderer-process-limit=1", "--disable-background-networking", "--disable-extensions"]
        });
        // Close the whole browser on cancellation, not just the caller's waiting task.
        using var cancellation = ct.Register(() => _ = CloseQuietlyAsync(browser));
        ct.ThrowIfCancellationRequested();
        await using var context = await browser.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true, AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block,
            ViewportSize = new() { Width = 960, Height = 600 }
        });
        var requests = 0;
        await context.RouteAsync("**/*", route => AllowedRequest(origin, route.Request.Url, route.Request.Method) &&
            Interlocked.Increment(ref requests) <= 100 ? route.ContinueAsync() : route.AbortAsync());
        await context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
        var page = await context.NewPageAsync();
        page.Dialog += async (_, dialog) =>
        {
            try { await dialog.DismissAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { /* A dialog racing browser shutdown must not become an unhandled async event. */ }
        };
        await page.GotoAsync(origin.AbsoluteUri, new() { Timeout = 7000, WaitUntil = WaitUntilState.DOMContentLoaded });
        await Task.Delay(500, ct);
        var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 65, Timeout = 3000, FullPage = false });
        ct.ThrowIfCancellationRequested();
        return bytes;
    }

    private static async Task CloseQuietlyAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { /* Cancellation can race browser startup, a page crash or normal disposal. */ }
    }

    public static bool AllowedRequest(Uri origin, string url, string method) => method == "GET" && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == origin.Scheme && uri.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == origin.Port && string.IsNullOrEmpty(uri.UserInfo);
}
