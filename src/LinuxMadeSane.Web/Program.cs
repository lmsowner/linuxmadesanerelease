using System.Net;
using System.Reflection;
using System.Security.Claims;
using LinuxMadeSane.Application;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Scheduling;
using LinuxMadeSane.Infrastructure;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Web.Components;
using LinuxMadeSane.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Web;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Any(argument =>
                argument.Equals("version", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(ResolveProductVersion());
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        ApplyDefaultUrls(builder);

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "lms.remote";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/access-denied";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ITransientConnectionSecretStore, TransientConnectionSecretStore>();
        builder.Services.AddSingleton<TerminalWorkspaceRegistry>();
        builder.Services.AddSingleton<FileBrowserWorkspaceRegistry>();
        builder.Services.AddSingleton<FileActionQueueService>();
        builder.Services.AddSingleton<BrowserFileTransferService>();
        builder.Services.AddSingleton<MediaLibrarySignedUrlService>();
        builder.Services.AddScoped<MediaLibraryTranscodePreviewService>();
        builder.Services.AddScoped<TerminalWorkspaceAccessor>();
        builder.Services.AddApplicationServices();
        builder.Services.AddInfrastructureServices(builder.Configuration, builder.Environment.ContentRootPath);
        PluginModuleLoader.ConfigureServices(builder);

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var initializer = scope.ServiceProvider.GetRequiredService<SqliteDatabaseInitializer>();
            initializer.InitializeAsync().GetAwaiter().GetResult();
        }

        if (TryHandleConsoleCommand(args, app.Services))
        {
            return;
        }

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        if (app.Environment.IsDevelopment())
        {
            app.UseWhen(
                context => !IsMediaLibraryApiPath(context.Request.Path),
                branch => branch.UseHttpsRedirection());
        }
        else
        {
            app.UseHttpsRedirection();
        }
        app.Use(async (context, next) =>
        {
            var workspaceId = context.Request.Cookies.TryGetValue(TerminalWorkspaceAccessor.CookieName, out var cookieValue) &&
                              !string.IsNullOrWhiteSpace(cookieValue)
                ? cookieValue
                : Guid.NewGuid().ToString("N");

            context.Items[TerminalWorkspaceAccessor.HttpContextItemKey] = workspaceId;

            if (!context.Request.Cookies.ContainsKey(TerminalWorkspaceAccessor.CookieName))
            {
                context.Response.Cookies.Append(
                    TerminalWorkspaceAccessor.CookieName,
                    workspaceId,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = context.Request.IsHttps
                    });
            }

            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (context, next) =>
        {
            if (IsAlwaysAnonymousAllowedPath(context.Request.Path))
            {
                await next();
                return;
            }

            var trustedNetworkAccessService = context.RequestServices.GetRequiredService<ITrustedNetworkAccessService>();
            var accessResult = await trustedNetworkAccessService.EvaluateAsync(
                context.Connection.RemoteIpAddress,
                context.Request.Host.Host,
                context.RequestAborted);
            context.Items["LmsTrustedNetworkAccess"] = accessResult;

            if (accessResult.IsTrusted ||
                (accessResult.RequiresAuthentication && context.User.Identity?.IsAuthenticated == true))
            {
                await next();
                return;
            }

            if (IsAuthenticationEntryPath(context.Request.Path))
            {
                if (accessResult.RequiresAuthentication)
                {
                    await next();
                    return;
                }

                if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.Redirect("/access-denied");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("No access from this network interface.");
                return;
            }

            if (!accessResult.IsAllowed)
            {
                if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.Redirect("/access-denied");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("No access from this network interface.");
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.Redirect(BuildLoginRedirectTarget(context.Request.Path, context.Request.QueryString.Value));
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            version = ResolveProductVersion()
        }));
        app.MapPost("/auth/login", async (HttpContext context, ISecurityAuthenticationService authenticationService) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var identifier = form["identifier"].ToString();
            var otpCode = form["otpCode"].ToString();
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

            var result = await authenticationService.ValidateOtpAsync(identifier, otpCode, context.RequestAborted);
            if (!result.Succeeded || !result.UserId.HasValue || string.IsNullOrWhiteSpace(result.Email))
            {
                context.Response.Redirect(BuildLoginRedirectTarget(returnUrl, result.FailureMessage, identifier));
                return;
            }

            Claim[] claims =
            [
                new Claim(ClaimTypes.NameIdentifier, result.UserId.Value.ToString()),
                new Claim(ClaimTypes.Name, result.Email),
                new Claim(ClaimTypes.Email, result.Email)
            ];

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
                });

            context.Response.Redirect(returnUrl);
        }).DisableAntiforgery();
        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        }).DisableAntiforgery();
        app.MapPost("/internal/scheduler/tasks/{taskId:guid}/run", async (
            HttpContext context,
            Guid taskId,
            IScheduledTaskService scheduledTaskService) =>
        {
            if (!IsLoopbackRequest(context.Connection.RemoteIpAddress))
            {
                return Results.Text("Loopback access only.", contentType: "text/plain", statusCode: StatusCodes.Status403Forbidden);
            }

            if (!context.Request.Headers.TryGetValue(ScheduledTaskTrigger.HeaderName, out var tokenValues) ||
                string.IsNullOrWhiteSpace(tokenValues.ToString()))
            {
                return Results.Text("Scheduled task trigger rejected.", contentType: "text/plain", statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                var result = await scheduledTaskService.TriggerTaskAsync(taskId, tokenValues.ToString(), context.RequestAborted);
                if (result.Success)
                {
                    return Results.NoContent();
                }

                var statusCode = result.Summary.Equals("Scheduled task trigger rejected.", StringComparison.Ordinal)
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status500InternalServerError;
                return Results.Text(result.Summary, contentType: "text/plain", statusCode: statusCode);
            }
            catch (Exception exception)
            {
                return Results.Text(exception.Message, contentType: "text/plain", statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery();
        app.MapPost("/internal/file-actions/uploads/{token}/chunks", async (
            HttpContext context,
            string token,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService) =>
        {
            if (!long.TryParse(context.Request.Query["offset"], out var offset) || offset < 0)
            {
                return Results.BadRequest("Upload chunk offset is required.");
            }

            var result = await browserFileTransferService.AppendUploadChunkAsync(token, offset, context.Request.Body, context.RequestAborted);
            fileActionQueueService.ReportBrowserUploadProgress(result.WorkspaceId, result.JobId, result.ItemId, result.BytesTransferred, result.TotalBytes);
            return Results.Ok(new { result.BytesTransferred, result.TotalBytes });
        }).DisableAntiforgery();
        app.MapPost("/internal/file-actions/uploads/{token}/complete", async (
            string token,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService,
            CancellationToken cancellationToken) =>
        {
            var result = await browserFileTransferService.CompleteUploadAsync(token, cancellationToken);
            fileActionQueueService.ReportBrowserUploadProgress(result.WorkspaceId, result.JobId, result.ItemId, result.BytesTransferred, result.TotalBytes);
            return Results.Ok();
        }).DisableAntiforgery();
        app.MapPost("/internal/file-actions/uploads/{token}/cancel", async (
            string token,
            BrowserTransferCancelRequest? request,
            BrowserFileTransferService browserFileTransferService,
            CancellationToken cancellationToken) =>
        {
            await browserFileTransferService.CancelUploadAsync(token, request?.Reason, cancellationToken);
            return Results.Ok();
        }).DisableAntiforgery();
        app.MapGet("/internal/file-actions/downloads/{token}/content", (
            string token,
            BrowserFileTransferService browserFileTransferService) =>
        {
            var artifact = browserFileTransferService.GetDownloadArtifact(token);
            var stream = new FileStream(artifact.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
            return Results.File(stream, artifact.ContentType, artifact.DownloadFileName);
        });
        app.MapPost("/internal/file-actions/downloads/{token}/progress", (
            string token,
            BrowserDownloadProgressUpdate request,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService) =>
        {
            var result = browserFileTransferService.ReportDownloadProgress(token, request.BytesTransferred);
            fileActionQueueService.ReportBrowserDownloadProgress(result.WorkspaceId, result.JobId, result.ItemId, result.BytesTransferred, result.TotalBytes);
            return Results.Ok();
        }).DisableAntiforgery();
        app.MapPost("/internal/file-actions/downloads/{token}/complete", async (
            string token,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService,
            CancellationToken cancellationToken) =>
        {
            var result = await browserFileTransferService.CompleteDownloadAsync(token, cancellationToken);
            fileActionQueueService.ReportBrowserDownloadProgress(result.WorkspaceId, result.JobId, result.ItemId, result.BytesTransferred, result.TotalBytes);
            return Results.Ok();
        }).DisableAntiforgery();
        app.MapPost("/internal/file-actions/downloads/{token}/cancel", async (
            string token,
            BrowserTransferCancelRequest? request,
            BrowserFileTransferService browserFileTransferService,
            CancellationToken cancellationToken) =>
        {
            await browserFileTransferService.FailDownloadAsync(token, request?.Reason, cancellationToken);
            return Results.Ok();
        }).DisableAntiforgery();
        app.MapMediaLibraryIntegrationApi();
        var componentEndpoint = app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        if (PluginModuleLoader.LoadedAssemblies.Count > 0)
        {
            componentEndpoint.AddAdditionalAssemblies(PluginModuleLoader.LoadedAssemblies.ToArray());
        }

        app.Run();
    }

    private static void ApplyDefaultUrls(WebApplicationBuilder builder)
    {
        var explicitUrls =
            builder.Configuration["URLS"] ??
            builder.Configuration["ASPNETCORE_URLS"] ??
            builder.Configuration["DOTNET_URLS"];

        if (!string.IsNullOrWhiteSpace(explicitUrls))
        {
            return;
        }

        var configuredUrls = builder.Configuration.GetSection("Server:Urls").Get<string[]>();
        if (configuredUrls is not { Length: > 0 })
        {
            return;
        }

        builder.WebHost.UseUrls(configuredUrls);
    }

    private static bool TryHandleConsoleCommand(string[] args, IServiceProvider services)
    {
        if (args.Any(argument =>
                argument.Equals("smoke-startup", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--smoke-startup", StringComparison.OrdinalIgnoreCase)))
        {
            SmokeStartupAsync(services).GetAwaiter().GetResult();
            return true;
        }

        if (args.Any(argument =>
                argument.Equals("unlock-security", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--unlock-security", StringComparison.OrdinalIgnoreCase)))
        {
            UnlockSecurityAsync(services).GetAwaiter().GetResult();
            return true;
        }

        return false;
    }

    private static async Task SmokeStartupAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
        var dataSource = dbContext.Database.GetDbConnection().DataSource;

        Console.WriteLine("Linux Made Sane startup smoke passed.");
        Console.WriteLine($"Version: {ResolveProductVersion()}");
        Console.WriteLine($"Database: {dataSource}");
        Console.WriteLine($"Managed hosts: {await dbContext.ManagedHosts.CountAsync()}");
    }

    private static string ResolveProductVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
               assembly.GetName().Version?.ToString() ??
               "unknown";
    }

    private static async Task UnlockSecurityAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ITrustedNetworkStore>();
        var entries = await store.ListAsync();
        var now = DateTimeOffset.UtcNow;
        var updated = 0;

        foreach (var entry in entries)
        {
            var matchesLoopback =
                LinuxMadeSane.Application.Services.TrustedNetworkMatcher.Match(IPAddress.Loopback, [entry]) is not null ||
                LinuxMadeSane.Application.Services.TrustedNetworkMatcher.Match(IPAddress.IPv6Loopback, [entry]) is not null;
            if (!matchesLoopback)
            {
                continue;
            }

            await store.SaveAsync(entry with
            {
                IsEnabled = true,
                IsTrustedAccessEnabled = true,
                IsAuthenticationEnabled = false,
                UpdatedAtUtc = now
            });
            updated++;
        }

        if (updated == 0)
        {
            await store.SaveAsync(new Core.Models.TrustedNetworkEntry(
                Guid.NewGuid(),
                "Console unlock loopback IPv4",
                "127.0.0.0/8",
                "Emergency localhost recovery rule.",
                true,
                true,
                false,
                false,
                now,
                now));
            await store.SaveAsync(new Core.Models.TrustedNetworkEntry(
                Guid.NewGuid(),
                "Console unlock loopback IPv6",
                "::1/128",
                "Emergency localhost recovery rule.",
                true,
                true,
                false,
                false,
                now,
                now));
        }

        Console.WriteLine("Linux Made Sane console access has been restored for localhost.");
        Console.WriteLine($"Updated security rules in: {dbContext.Database.GetDbConnection().DataSource}");
        Console.WriteLine("Open http://127.0.0.1:5080 on the server console, or use SSH port forwarding, then fix the Security rules.");
    }

    private static bool IsAlwaysAnonymousAllowedPath(PathString path)
    {
        if (!path.HasValue)
        {
            return true;
        }

        if (path.StartsWithSegments("/access-denied") ||
            path.StartsWithSegments("/healthz") ||
            path.StartsWithSegments("/api/integrations/media-library") ||
            path.StartsWithSegments("/internal/scheduler") ||
            path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/_content") ||
            path.StartsWithSegments("/scripts") ||
            path.StartsWithSegments("/styles") ||
            path.StartsWithSegments("/lib") ||
            path.StartsWithSegments("/Error") ||
            path.StartsWithSegments("/not-found"))
        {
            return true;
        }

        var value = path.Value ?? string.Empty;
        return value.Equals("/favicon.png", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/app.css", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/LinuxMadeSane.Web.styles.css", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMediaLibraryApiPath(PathString path) =>
        path.StartsWithSegments("/api/integrations/media-library");

    private static bool IsAuthenticationEntryPath(PathString path) =>
        path.StartsWithSegments("/login") ||
        path.StartsWithSegments("/auth");

    private static bool IsLoopbackRequest(IPAddress? remoteIpAddress) =>
        remoteIpAddress is not null && IPAddress.IsLoopback(remoteIpAddress);

    private static string BuildLoginRedirectTarget(PathString path, string? queryString) =>
        BuildLoginRedirectTarget(NormalizeReturnUrl($"{path}{queryString}"), null, null);

    private static string BuildLoginRedirectTarget(string returnUrl, string? errorMessage, string? email)
    {
        var queryParts = new List<string>
        {
            $"returnUrl={Uri.EscapeDataString(NormalizeReturnUrl(returnUrl))}"
        };

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            queryParts.Add($"error={Uri.EscapeDataString(errorMessage)}");
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            queryParts.Add($"email={Uri.EscapeDataString(email.Trim())}");
        }

        return $"/login?{string.Join("&", queryParts)}";
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var trimmed = returnUrl.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return trimmed;
    }
}
