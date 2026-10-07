// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using Microsoft.AspNetCore.SignalR;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using LinuxMadeSane.Application;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Contracts.DesktopAssistant;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services.EdgeGateway;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.DesktopSession;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Core.Models.Scheduling;
using LinuxMadeSane.Core.Versioning;
using LinuxMadeSane.Infrastructure;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Services;
using LinuxMadeSane.Web.Components;
using LinuxMadeSane.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace LinuxMadeSane.Web;

public class Program
{
    private const string AntiforgeryCookieName = "lms.antiforgery";
    private const string OriginalConnectionRemoteIpAddressItemKey = "LmsOriginalConnectionRemoteIpAddress";
    private const string OriginalRequestHostItemKey = "LmsOriginalRequestHost";

    public static void Main(string[] args)
    {
        if (TryHandlePrivilegedDriveUsageCommand(args))
        {
            return;
        }

        var launchDirectory = Environment.CurrentDirectory;

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
            .AddInteractiveServerComponents()
            .AddHubOptions(options => options.AddFilter<ConnectedUserHubFilter>());
        builder.Services.AddSingleton<ConnectedUserRegistry>();
        builder.Services.AddSingleton<ConnectedUserDnsResolver>();
        builder.Services.AddScoped<LmsHostEdgeGatewaySummaryService>();
        builder.Services.AddSingleton<IBenchmarkProcessRunner, BenchmarkProcessRunner>();
        builder.Services.AddSingleton<PerformanceBenchmarkService>();
        builder.Services.AddScoped<LmsHostBenchmarkService>();
        builder.Services.AddScoped<OverviewRefreshPreference>();
        builder.Services.AddSingleton<ConnectedUserHubFilter>();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddRequiredComponentServices(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                       ForwardedHeaders.XForwardedHost |
                                       ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "lms.remote";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/access-denied";
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = ValidateRemoteSessionAsync
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("lms-auth-start", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    BuildRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0
                    }));
            options.AddPolicy("lms-auth-verify", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    BuildRateLimitPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0
                    }));
        });
        builder.Services.AddSingleton<ITransientConnectionSecretStore, TransientConnectionSecretStore>();
        builder.Services.AddSingleton<DesktopAssistantLaunchTicketStore>();
        builder.Services.AddSingleton<DesktopAssistantNativeThemeStore>();
        builder.Services.AddSingleton<IDesktopAssistantLaunchTicketStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DesktopAssistantLaunchTicketStore>());
        builder.Services.AddSingleton<IDesktopAssistantLaunchTicketIssuer>(serviceProvider =>
            serviceProvider.GetRequiredService<DesktopAssistantLaunchTicketStore>());
        builder.Services.AddSingleton<TerminalWorkspaceRegistry>();
        builder.Services.AddScoped<HomeLabTerminalTaskLauncher>();
        builder.Services.AddScoped<SshMountTerminalTaskLauncher>();
        builder.Services.AddScoped<HomeLabTerminalCommandService>();
        builder.Services.AddSingleton<FileBrowserWorkspaceRegistry>();
        builder.Services.AddSingleton<FileActionQueueService>();
        builder.Services.AddSingleton<BrowserFileTransferService>();
        builder.Services.AddScoped<ShareMountsWorkspaceService>();
        builder.Services.AddScoped<ConnectionProfileUserResolver>();
        builder.Services.AddScoped<ISavedCredentialAccessContext, SavedCredentialAccessContext>();
        builder.Services.AddScoped<IHostAdministratorCredentials, HostAdministratorCredentials>();
        builder.Services.AddSingleton<MediaLibrarySignedUrlService>();
        builder.Services.AddSingleton<RemoteLmsTunnelAccessService>();
        builder.Services.AddSingleton<RemoteLmsRelayCaddyService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<RemoteLmsRelayCaddyService>());
        builder.Services.AddSingleton<RemoteLmsSshTunnelService>();
        builder.Services.AddSingleton<OnDemandAppLaunchTicketStore>();
        builder.Services.AddSingleton<OnDemandAppLaunchCoordinator>();
        builder.Services.AddSingleton<HttpServiceDiscoveryCoordinator>();
        builder.Services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<HttpServiceDiscoveryCoordinator>());
        builder.Services.AddHostedService<EdgeGatewayConfigurationStartupService>();
        builder.Services.AddHostedService<OnDemandAppCleanupHostedService>();
        builder.Services.AddScoped<LocalInstanceIdentityService>();
        builder.Services.AddScoped<PasskeyAuthenticationService>();
        builder.Services.Configure<ApplicationUpdateOptions>(builder.Configuration.GetSection("ApplicationUpdates"));
        builder.Services.AddHttpClient("ApplicationUpdates", client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton(provider =>
            new ApplicationUpdateService(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("ApplicationUpdates"),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ApplicationUpdateOptions>>(),
                provider.GetRequiredService<ILogger<ApplicationUpdateService>>(),
                new ApplicationReleaseChannel(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(builder.Configuration.GetConnectionString("LinuxMadeSane") ?? "Data Source=data/linuxmadesane.db").DataSource,
                    builder.Environment.ContentRootPath))!, "update-channel.json"))));
        builder.Services.AddHttpClient<LmsHostUpdateAvailabilityService>()
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        builder.Services.AddHostedService<ApplicationUpdateHostedService>();
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

        RegisterStartupConsoleSummary(app);

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.Use(async (context, next) =>
        {
            ConnectedUserClientAddress.Capture(context);
            context.Items[OriginalConnectionRemoteIpAddressItemKey] = context.Connection.RemoteIpAddress;
            context.Items[OriginalRequestHostItemKey] = context.Request.Host;
            await next();
        });
        app.UseForwardedHeaders();
        app.UseLocalAiPeerApiPortIsolation();
        var forceHttpsRedirection = IsHttpsRedirectionForced(app.Configuration);
        var isDevelopment = app.Environment.IsDevelopment();
        app.UseWhen(
            context => ShouldApplyHttpsRedirection(context, isDevelopment, forceHttpsRedirection),
            branch => branch.UseHttpsRedirection());
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
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Value?.Equals("/setup", StringComparison.OrdinalIgnoreCase) == true &&
                !context.Request.IsHttps &&
                bool.TryParse(context.RequestServices.GetRequiredService<IConfiguration>()["Setup:RequireHttps"], out var requireHttps) &&
                requireHttps)
            {
                var host = context.Request.Host.Host;
                if (host.Contains(":", StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
                {
                    host = $"[{host}]";
                }

                var port = context.RequestServices.GetRequiredService<IConfiguration>()["Setup:HttpsPort"] ?? "5443";
                context.Response.Redirect($"https://{host}:{port}{context.Request.Path}{context.Request.QueryString}");
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Value?.Equals("/setup", StringComparison.OrdinalIgnoreCase) == true)
            {
                var recoveryService = context.RequestServices.GetRequiredService<LocalAccessRecoveryService>();
                if (!await recoveryService.HasActiveTemporarySetupAsync(context.RequestAborted))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next();
        });

        app.Use(async (context, next) =>
        {
            if (IsAlwaysAnonymousAllowedPath(context.Request.Path))
            {
                await next();
                return;
            }

            if (await HasTemporaryRecoveryAccessAsync(context))
            {
                // The installer code grants temporary LMS access without requiring an
                // account. Account/MFA enrollment remains an optional recovery task.
                context.Items["LmsTrustedNetworkAccess"] = new TrustedNetworkAccessResult(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    context.Request.Host.Host, true, "Temporary setup code", true,
                    false, true, true, false);
                await next();
                return;
            }

            var remoteTunnelAccessService = context.RequestServices.GetRequiredService<RemoteLmsTunnelAccessService>();
            if (remoteTunnelAccessService.IsAuthorized(
                    context.Connection.RemoteIpAddress,
                    context.Request.Cookies[RemoteLmsTunnelAccessService.CookieName]))
            {
                context.Items["LmsTrustedNetworkAccess"] = new TrustedNetworkAccessResult(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    context.Request.Host.Host,
                    true,
                    "LMS SSH tunnel",
                    true,
                    false,
                    true,
                    true,
                    false);
                await next();
                return;
            }

            var trustedNetworkAccessService = context.RequestServices.GetRequiredService<ITrustedNetworkAccessService>();
            var accessResult = await trustedNetworkAccessService.EvaluateAsync(
                context.Connection.RemoteIpAddress,
                context.Request.Host.Host,
                context.RequestAborted);
            accessResult = await TryEvaluateNoAccessCloudflareLocalExposureAsync(
                context,
                trustedNetworkAccessService) ?? accessResult;
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

                await RespondToDeniedNetworkRequestAsync(context, accessResult);
                return;
            }

            if (context.Request.Path.StartsWithSegments("/access-denied"))
            {
                if (accessResult.DeniedResponseMode == NetworkAccessDeniedResponseMode.EmptyNotFound)
                {
                    await RespondToDeniedNetworkRequestAsync(context, accessResult);
                    return;
                }

                await next();
                return;
            }

            // Recovery does not permanently bypass interface policy. If access is
            // still protected when the recovery session ends, return to its code entry.
            // Trusted/direct access and signed-in sessions were already admitted above.
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
                await context.RequestServices.GetRequiredService<LocalAccessRecoveryService>()
                    .HasActiveTemporarySetupAsync(context.RequestAborted))
            {
                context.Response.Redirect(BuildInitialSetupRedirectTarget(
                    NormalizeReturnUrl($"{context.Request.Path}{context.Request.QueryString}")));
                return;
            }

            if (!accessResult.IsAllowed)
            {
                await RespondToDeniedNetworkRequestAsync(context, accessResult);
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.Redirect(BuildLoginRedirectTarget(context.Request.Path, context.Request.QueryString.Value));
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

        app.UseRateLimiter();
        app.UseAntiforgery();

        app.MapStaticAssets();
        // The gateway already allows this prefix before authentication, including after a restart.
        app.MapGet(EdgeGatewayAuthenticationPaths.Availability, async (
            HttpContext context,
            ITrustedNetworkAccessService trustedNetworkAccessService,
            RemoteLmsTunnelAccessService remoteTunnelAccessService) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            // This endpoint is intentionally anonymous so a browser can detect LMS after a
            // restart. Evaluate access here rather than relying on the normal access middleware,
            // which deliberately skips this path.
            var requiresAuthentication = !remoteTunnelAccessService.IsAuthorized(
                context.Connection.RemoteIpAddress,
                context.Request.Cookies[RemoteLmsTunnelAccessService.CookieName]) &&
                !await HasTemporaryRecoveryAccessAsync(context);
            if (requiresAuthentication)
            {
                var accessResult = await trustedNetworkAccessService.EvaluateAsync(
                    context.Connection.RemoteIpAddress,
                    context.Request.Host.Host,
                    context.RequestAborted);
                accessResult = await TryEvaluateNoAccessCloudflareLocalExposureAsync(
                    context,
                    trustedNetworkAccessService) ?? accessResult;
                requiresAuthentication = accessResult.RequiresAuthentication;
            }

            return Results.Json(new
            {
                status = "ok",
                product = "linux-made-sane",
                requiresAuthentication
            });
        });
        app.MapGet("/healthz", (ApplicationUpdateService updates) => Results.Json(new
        {
            status = "ok",
            product = "linux-made-sane",
            name = "Linux Made Sane",
            supportsManagedUpdates = true,
            version = ResolveProductVersion(),
            releaseChannel = updates.GetStatus().Channel
        }));
        app.MapGet("/on-demand-apps/open", (
            HttpContext context,
            OnDemandAppLaunchCoordinator launches,
            string? service,
            Guid? lease) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var email = context.User.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(email))
            {
                return Results.Unauthorized();
            }

            var jobId = launches.Start(new OnDemandAppLaunchRequest(
                service ?? string.Empty,
                lease.GetValueOrDefault() == Guid.Empty ? Guid.NewGuid() : lease!.Value,
                userId,
                email,
                context.Request.Host.Host,
                context.Request.IsHttps));
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content(BuildOnDemandAppLaunchProgressHtml(jobId), "text/html", Encoding.UTF8);
        }).RequireAuthorization();
        app.MapGet("/on-demand-apps/launch/{jobId:guid}/status", (
            HttpContext context,
            Guid jobId,
            OnDemandAppLaunchCoordinator launches) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var snapshot = launches.GetSnapshot(jobId, userId);
            return snapshot is null ? Results.NotFound() : Results.Json(snapshot);
        }).RequireAuthorization();
        app.MapGet("/on-demand-apps/launch/{jobId:guid}/complete", async (
            HttpContext context,
            Guid jobId,
            OnDemandAppLaunchCoordinator launches,
            OnDemandAppLaunchTicketStore launchTickets) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var snapshot = launches.GetSnapshot(jobId, userId);
            if (snapshot is null)
            {
                return Results.NotFound();
            }

            if (snapshot.State == "failed")
            {
                return Results.Content(
                    BuildOnDemandAppLaunchFailureHtml(snapshot.Error ?? "The temporary app connection could not be created."),
                    "text/html",
                    Encoding.UTF8,
                    StatusCodes.Status400BadRequest);
            }

            var launch = launches.GetCompletedLaunch(jobId, userId);
            if (launch is null)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            context.Response.Headers["Content-Security-Policy"] =
                $"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action https://{launch.Hostname}";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = launchTickets.Issue(
                context.User,
                launch.Hostname,
                authentication.Properties?.IssuedUtc,
                authentication.Properties?.ExpiresUtc);
            return Results.Content(BuildOnDemandAppLaunchHtml(launch, ticket), "text/html", Encoding.UTF8);
        }).RequireAuthorization();
        app.MapPost("/edge-auth/on-demand", async (
            HttpContext context,
            OnDemandAppLaunchTicketStore launchTickets) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var ticket = launchTickets.Consume(form["ticket"].ToString(), context.Request.Host.Host);
            if (ticket is null)
            {
                return Results.Unauthorized();
            }

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ticket.Principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = false,
                    IssuedUtc = ticket.SessionIssuedUtc ?? DateTimeOffset.UtcNow,
                    ExpiresUtc = ticket.SessionExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(12)
                });
            // This endpoint is reached through the temporary app hostname. Use that
            // ticket-bound hostname explicitly so the browser can never fall back to
            // the LMS host when it follows the post-authentication redirect.
            return Results.Redirect($"https://{ticket.ExpectedHost}/");
        }).DisableAntiforgery();
        app.MapPost("/on-demand-apps/lease/{leaseId:guid}/heartbeat", async (
            HttpContext context,
            Guid leaseId,
            OnDemandAppService onDemandApps) =>
        {
            var email = context.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
            return await onDemandApps.TouchAsync(leaseId, email, context.RequestAborted)
                ? Results.NoContent()
                : Results.NotFound();
        }).DisableAntiforgery().RequireAuthorization();
        app.MapPost("/on-demand-apps/lease/{leaseId:guid}/release", async (
            HttpContext context,
            Guid leaseId,
            OnDemandAppService onDemandApps) =>
        {
            var email = context.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
            return await onDemandApps.ReleaseAsync(leaseId, email, context.RequestAborted)
                ? Results.NoContent()
                : Results.NotFound();
        }).DisableAntiforgery().RequireAuthorization();
        app.MapPost("/auth/setup/authorize", async (
            HttpContext context,
            LocalAccessRecoveryService recoveryService,
            TemporarySetupAuthorizationService setupAuthorization) =>
        {
            if (!context.Request.IsHttps &&
                bool.TryParse(context.RequestServices.GetRequiredService<IConfiguration>()["Setup:RequireHttps"], out var requireHttps) &&
                requireHttps)
            {
                return Results.BadRequest("Initial setup must be completed over HTTPS.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
            var result = await recoveryService.ConsumeTemporarySetupCodeAsync(
                form["temporarySetupCode"].ToString(),
                context.Connection.RemoteIpAddress?.ToString(),
                context.RequestAborted);
            if (!result.Succeeded)
            {
                return Results.Redirect(BuildInitialSetupRedirectTarget(returnUrl, result.ErrorMessage));
            }

            setupAuthorization.Authorize(context.Response, context.Request.IsHttps);
            return Results.Redirect("/settings?tab=trusted-networks");
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapGet("/desktop-assistant/launch", (
            HttpContext context,
            string? ticket,
            IDesktopAssistantLaunchTicketStore launchTicketStore) =>
        {
            if (!IsOriginalLoopbackRequest(context) ||
                !IsOriginalLoopbackRequestHost(context))
            {
                return Results.NotFound();
            }

            if (!launchTicketStore.TryConsume(ticket, out var safeReturnUrl))
            {
                safeReturnUrl = "/desktop-assistant?fromTray=1";
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";

            return Results.Redirect(safeReturnUrl);
        });
        app.MapGet("/api/desktop-assistant/native/workspace", async (
            HttpContext context,
            Guid? sessionId,
            IDesktopAssistantLaunchTicketStore launchTicketStore,
            DesktopAssistantNativeThemeStore themeStore,
            IDesktopAssistantChatService desktopAssistantChatService,
            IAiThreadService threadService,
            IDesktopSessionBroker desktopSessionBroker) =>
        {
            if (!TryAuthorizeDesktopAssistantNativeRequest(context, launchTicketStore, out var rejection))
            {
                return rejection;
            }

            var providerContext = await threadService.GetEditorAsync(cancellationToken: context.RequestAborted);
            var workspace = await desktopAssistantChatService.GetWorkspaceAsync(
                sessionId,
                desktopSessionBroker.GetSnapshot(),
                context.RequestAborted);
            return Results.Json(MapDesktopAssistantNativeWorkspace(workspace, providerContext, themeStore.Current));
        }).DisableAntiforgery();
        app.MapPost("/api/desktop-assistant/native/sessions", async (
            HttpContext context,
            IDesktopAssistantLaunchTicketStore launchTicketStore,
            DesktopAssistantNativeThemeStore themeStore,
            IDesktopAssistantChatService desktopAssistantChatService,
            IAiThreadService threadService,
            IDesktopSessionBroker desktopSessionBroker) =>
        {
            if (!TryAuthorizeDesktopAssistantNativeRequest(context, launchTicketStore, out var rejection))
            {
                return rejection;
            }

            var request = await context.Request.ReadFromJsonAsync<DesktopAssistantNativeCreateSessionRequest>(
                cancellationToken: context.RequestAborted) ?? new DesktopAssistantNativeCreateSessionRequest(null, null);
            var sessionId = await desktopAssistantChatService.CreateSessionAsync(
                request.ProviderKey,
                request.ModelId,
                context.RequestAborted);
            var providerContext = await threadService.GetEditorAsync(cancellationToken: context.RequestAborted);
            var workspace = await desktopAssistantChatService.GetWorkspaceAsync(
                sessionId,
                desktopSessionBroker.GetSnapshot(),
                context.RequestAborted);
            return Results.Json(MapDesktopAssistantNativeWorkspace(workspace, providerContext, themeStore.Current));
        }).DisableAntiforgery();
        app.MapDelete("/api/desktop-assistant/native/sessions/{sessionId:guid}", async (
            HttpContext context,
            Guid sessionId,
            IDesktopAssistantLaunchTicketStore launchTicketStore,
            DesktopAssistantNativeThemeStore themeStore,
            IDesktopAssistantChatService desktopAssistantChatService,
            IAiThreadService threadService,
            IDesktopSessionBroker desktopSessionBroker) =>
        {
            if (!TryAuthorizeDesktopAssistantNativeRequest(context, launchTicketStore, out var rejection))
            {
                return rejection;
            }

            var providerContext = await threadService.GetEditorAsync(cancellationToken: context.RequestAborted);
            var workspace = await desktopAssistantChatService.DeleteSessionAsync(
                sessionId,
                desktopSessionBroker.GetSnapshot(),
                context.RequestAborted);
            return Results.Json(MapDesktopAssistantNativeWorkspace(workspace, providerContext, themeStore.Current));
        }).DisableAntiforgery();
        app.MapPost("/api/desktop-assistant/native/messages", async (
            HttpContext context,
            IDesktopAssistantLaunchTicketStore launchTicketStore,
            DesktopAssistantNativeThemeStore themeStore,
            IDesktopAssistantChatService desktopAssistantChatService,
            IAiThreadService threadService,
            IDesktopSessionBroker desktopSessionBroker) =>
        {
            if (!TryAuthorizeDesktopAssistantNativeRequest(context, launchTicketStore, out var rejection))
            {
                return rejection;
            }

            var request = await context.Request.ReadFromJsonAsync<DesktopAssistantNativeSendMessageRequest>(
                cancellationToken: context.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest();
            }

            var providerContext = await threadService.GetEditorAsync(cancellationToken: context.RequestAborted);
            var workspace = await desktopAssistantChatService.SendMessageAsync(
                request.SessionId,
                request.Message,
                desktopSessionBroker.GetSnapshot(),
                request.ProviderKey,
                request.ModelId,
                context.RequestAborted);
            return Results.Json(MapDesktopAssistantNativeWorkspace(workspace, providerContext, themeStore.Current));
        }).DisableAntiforgery();
        app.MapPost("/api/desktop-assistant/native/fixes/approve", async (
            HttpContext context,
            IDesktopAssistantLaunchTicketStore launchTicketStore,
            DesktopAssistantNativeThemeStore themeStore,
            IDesktopAssistantChatService desktopAssistantChatService,
            IAiThreadService threadService,
            IDesktopSessionBroker desktopSessionBroker) =>
        {
            if (!TryAuthorizeDesktopAssistantNativeRequest(context, launchTicketStore, out var rejection))
            {
                return rejection;
            }

            var request = await context.Request.ReadFromJsonAsync<DesktopAssistantNativeApproveFixRequest>(
                cancellationToken: context.RequestAborted);
            if (request?.Fix is null)
            {
                return Results.BadRequest();
            }

            var snapshot = desktopSessionBroker.GetSnapshot();
            var providerContext = await threadService.GetEditorAsync(cancellationToken: context.RequestAborted);
            DesktopAssistantChatWorkspaceViewModel workspace;
            if (request.Fix.Kind == DesktopSessionActionKinds.SetKeyboardLayout)
            {
                workspace = await desktopAssistantChatService.ApplyKeyboardLayoutAsync(
                    request.SessionId,
                    request.Fix.Arguments.GetValueOrDefault("layout") ?? string.Empty,
                    snapshot,
                    request.ProviderKey,
                    request.ModelId,
                    context.RequestAborted);
            }
            else if (request.Fix.Kind == DesktopSessionActionKinds.InstallAptPackages)
            {
                var packageNames = (request.Fix.Arguments.GetValueOrDefault("packages") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                workspace = await desktopAssistantChatService.InstallAptPackagesAsync(
                    request.SessionId,
                    packageNames,
                    snapshot,
                    request.ProviderKey,
                    request.ModelId,
                    context.RequestAborted);
            }
            else if (request.Fix.Kind == DesktopSessionActionKinds.RepairAptSources)
            {
                workspace = await desktopAssistantChatService.RepairAptSourcesAsync(
                    request.SessionId,
                    request.Fix.Arguments,
                    snapshot,
                    request.ProviderKey,
                    request.ModelId,
                    context.RequestAborted);
            }
            else
            {
                return Results.BadRequest();
            }

            return Results.Json(MapDesktopAssistantNativeWorkspace(workspace, providerContext, themeStore.Current));
        }).DisableAntiforgery();
        app.MapPost("/internal/lms-tunnel/grants", async (
            HttpContext context,
            RemoteLmsTunnelAccessService tunnelAccessService) =>
        {
            if (!IsLoopbackRequest(context.Connection.RemoteIpAddress) ||
                !IsLoopbackRequestHost(context.Request.Host))
            {
                return Results.NotFound();
            }

            var request = await context.Request.ReadFromJsonAsync<RemoteLmsTunnelGrantRequest>(
                cancellationToken: context.RequestAborted) ?? new RemoteLmsTunnelGrantRequest("/");
            var grant = tunnelAccessService.IssueGrant(request.ReturnUrl);
            return Results.Json(new RemoteLmsTunnelGrantResponse(grant.Token, grant.ExpiresAtUtc));
        }).DisableAntiforgery();
        app.MapPost("/internal/lms-tunnel/update", async (
            HttpContext context, RemoteLmsTunnelAccessService tunnelAccessService,
            ApplicationUpdateService updates) =>
        {
            if (!IsLoopbackRequest(context.Connection.RemoteIpAddress) || !IsLoopbackRequestHost(context.Request.Host))
                return Results.NotFound();
            var request = await context.Request.ReadFromJsonAsync<RemoteLmsUpdateRequest>(cancellationToken: context.RequestAborted);
            if (request is null || !tunnelAccessService.AuthorizeUpdate(context.Connection.RemoteIpAddress, context.Request.Host.Host, request.Token))
                return Results.Unauthorized();
            // No caller-supplied command, account or channel: the remote LMS owns
            // its updater and preserves its own selected release channel.
            return Results.Json(await updates.InstallLatestAsync(context.RequestAborted));
        }).DisableAntiforgery();
        app.MapGet("/internal/lms-tunnel/consume", (
            HttpContext context,
            string? token,
            RemoteLmsTunnelAccessService tunnelAccessService) =>
        {
            if (!IsLoopbackRequest(context.Connection.RemoteIpAddress))
            {
                return Results.Redirect("/access-denied");
            }

            var session = tunnelAccessService.ConsumeGrant(token);
            if (session is null)
            {
                return Results.Redirect("/access-denied");
            }

            context.Response.Cookies.Append(
                RemoteLmsTunnelAccessService.CookieName,
                session.SessionToken,
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    Path = "/",
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Expires = session.ExpiresAtUtc
                });

            return Results.Redirect(session.ReturnUrl);
        }).DisableAntiforgery();
        app.MapGet("/edge-auth/check", async Task (
            HttpContext context,
            IEdgeGatewayService edgeGatewayService,
            ITrustedNetworkAccessService trustedNetworkAccessService,
            OnDemandAppService onDemandApps) =>
        {
            // Caddy supplies the original source for its local forward-auth request.
            var forwardedSource = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            var sourceAddress = IPAddress.TryParse(forwardedSource, out var forwardedAddress)
                ? forwardedAddress : context.Connection.RemoteIpAddress;
            var interfaceAccess = await trustedNetworkAccessService.EvaluateAsync(sourceAddress,
                context.Request.Headers["X-Forwarded-Host"].ToString(), context.RequestAborted);
            var result = await edgeGatewayService.EvaluateAuthAsync(
                new EdgeGatewayAuthCheckContext(
                    context.Request.Headers["X-Forwarded-Host"].ToString(),
                    context.Request.Headers["X-Forwarded-Proto"].ToString(),
                    context.Request.Headers["X-Forwarded-Uri"].ToString(),
                    context.Request.Headers["X-Forwarded-For"].ToString(),
                    context.Request.Headers.Host.ToString(),
                    context.Connection.RemoteIpAddress,
                    context.User,
                    context.Request.Headers["CF-IPCountry"].ToString(),
                    context.Request.Headers.UserAgent.ToString(),
                    await HasTemporaryRecoveryAccessAsync(context),
                    await context.RequestServices.GetRequiredService<LocalAccessRecoveryService>()
                        .HasActiveTemporarySetupAsync(context.RequestAborted),
                    interfaceAccess.IsTrusted),
                context.RequestAborted);

            context.Response.StatusCode = result.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";

            if (!string.IsNullOrWhiteSpace(result.RedirectLocation))
            {
                context.Response.Headers.Location = result.RedirectLocation;
            }

            if (result.StatusCode == StatusCodes.Status200OK)
            {
                if (!string.IsNullOrWhiteSpace(result.UserName))
                {
                    context.Response.Headers["X-LMS-User"] = result.UserName;
                }

                if (!string.IsNullOrWhiteSpace(result.UserEmail))
                {
                    context.Response.Headers["X-LMS-Email"] = result.UserEmail;
                }

                if (!string.IsNullOrWhiteSpace(result.Groups))
                {
                    context.Response.Headers["X-LMS-Groups"] = result.Groups;
                }

                if (!string.IsNullOrWhiteSpace(result.UserEmail))
                {
                    _ = await onDemandApps.TouchByHostnameAsync(
                        context.Request.Headers["X-Forwarded-Host"].ToString(),
                        result.UserEmail,
                        context.RequestAborted);
                }
            }
        }).DisableAntiforgery();
        app.MapGet("/edge-auth/approve-ip", async (
            string? token,
            IEdgeGatewayService edgeGatewayService,
            CancellationToken cancellationToken) =>
        {
            var result = await edgeGatewayService.ApproveTemporaryIpAsync(token ?? string.Empty, cancellationToken);
            return Results.Content(BuildTemporaryIpApprovalHtml(result), "text/html", Encoding.UTF8);
        });
        app.MapGet("/edge-auth/block-ip", async (
            string? token, HttpContext context, IAntiforgery antiforgery,
            IEdgeGatewayService edgeGatewayService, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var result = await edgeGatewayService.BlockTemporaryIpAsync(token ?? string.Empty, false, cancellationToken);
            var protection = result.Success ? antiforgery.GetAndStoreTokens(context) : null;
            return Results.Content(BuildTemporaryIpApprovalHtml(result, result.Success ? token : null,
                protection?.FormFieldName, protection?.RequestToken), "text/html", Encoding.UTF8);
        });
        app.MapPost("/edge-auth/block-ip", async (
            HttpContext context, IAntiforgery antiforgery, IEdgeGatewayService edgeGatewayService,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!context.Request.HasFormContentType) return Results.BadRequest("Open the block link from your email to confirm.");
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Confirmation expired. Reopen the block link from your email."); }
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var result = await edgeGatewayService.BlockTemporaryIpAsync(form["token"].ToString(), true, cancellationToken);
            return Results.Content(BuildTemporaryIpApprovalHtml(result), "text/html", Encoding.UTF8);
        });
        app.MapGet("/edge-auth/return", async (
            string? target,
            IEdgeGatewayService edgeGatewayService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(target) ||
                !await edgeGatewayService.IsSafeReturnTargetAsync(target, cancellationToken))
            {
                return Results.Redirect("/");
            }

            return Results.Redirect(target);
        });
        app.MapPost("/auth/initial-setup/start", async (
            HttpContext context,
            ISecuritySettingsService securitySettingsService,
            TemporarySetupAuthorizationService setupAuthorization) =>
        {
            if (!setupAuthorization.IsAuthorized(context.Request))
            {
                return Results.Redirect("/setup?error=Enter%20the%20Temporary%20Setup%20Code%20first.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString();
            var linuxUsername = form["linuxUsername"].ToString();
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

            try
            {
                await securitySettingsService.StartInitialSetupAsync(
                    new SecurityUserEditor
                    {
                        Email = email,
                        LinuxUsername = linuxUsername,
                        SessionLifetimeMinutes = SecuritySessionPolicy.DefaultSessionLifetimeMinutes,
                        SshAuthenticationMode = RemoteAccessSshAuthenticationMode.Password
                    },
                    BuildAbsoluteLoginUrl(context, email),
                    context.RequestAborted);

                return Results.Redirect(BuildInitialSetupRedirectTarget(returnUrl));
            }
            catch (Exception exception)
            {
                return Results.Redirect(BuildInitialSetupRedirectTarget(returnUrl, exception.Message, email, linuxUsername));
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-start");
        app.MapPost("/auth/initial-setup/reset", async (
            HttpContext context,
            ISecuritySettingsService securitySettingsService,
            TemporarySetupAuthorizationService setupAuthorization) =>
        {
            if (!setupAuthorization.IsAuthorized(context.Request))
            {
                return Results.Redirect("/setup?error=Enter%20the%20Temporary%20Setup%20Code%20first.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

            try
            {
                await securitySettingsService.ResetInitialSetupOtpAsync(
                    BuildAbsoluteLoginUrl(context, null),
                    context.RequestAborted);

                return Results.Redirect(BuildInitialSetupRedirectTarget(returnUrl));
            }
            catch (Exception exception)
            {
                return Results.Redirect(BuildInitialSetupRedirectTarget(returnUrl, exception.Message));
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-start");
        app.MapPost("/auth/initial-setup/verify", async (
            HttpContext context,
            ISecuritySettingsService securitySettingsService,
            PasskeyAuthenticationService passkeyAuthenticationService,
            TemporarySetupAuthorizationService setupAuthorization) =>
        {
            if (!setupAuthorization.IsAuthorized(context.Request))
            {
                return Results.Redirect("/setup?error=Enter%20the%20Temporary%20Setup%20Code%20first.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
            if (!Guid.TryParse(form["userId"].ToString(), out var userId))
            {
                return Results.Redirect(BuildInitialSetupRedirectTarget(returnUrl, "The pending setup user was not valid."));
            }

            var otpCode = form["otpCode"].ToString();
            var result = await securitySettingsService.ConfirmInitialSetupOtpAsync(
                userId,
                otpCode,
                context.RequestAborted);
            if (!result.Succeeded || !result.UserId.HasValue || string.IsNullOrWhiteSpace(result.Email))
            {
                return Results.Redirect(BuildInitialSetupRedirectTarget(
                    returnUrl,
                    result.FailureMessage ?? "The MFA code was not valid.",
                    form["email"].ToString()));
            }

            Claim[] claims =
            [
                new Claim(ClaimTypes.NameIdentifier, result.UserId.Value.ToString()),
                new Claim(ClaimTypes.Name, result.Email),
                new Claim(ClaimTypes.Email, result.Email),
                new Claim("lms:mfa", "true"),
                new Claim("amr", "otp")
            ];

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            var sessionLifetime = TimeSpan.FromMinutes(
                SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.SessionLifetimeMinutes));
            var issuedAtUtc = DateTimeOffset.UtcNow;
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(
                "auth_time",
                issuedAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = false,
                    IssuedUtc = issuedAtUtc,
                    ExpiresUtc = issuedAtUtc.Add(sessionLifetime)
                });

            if (IsPasskeyCapableRequest(context) &&
                await passkeyAuthenticationService.ShouldOfferPasskeySetupAsync(
                    result.UserId.Value,
                    context.RequestAborted))
            {
                return Results.Redirect(BuildPasskeySetupRedirectTarget(returnUrl));
            }

            return Results.Redirect(returnUrl);
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost("/auth/login", async (
            HttpContext context,
            ISecurityAuthenticationService authenticationService,
            PasskeyAuthenticationService passkeyAuthenticationService) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString();
            if (string.IsNullOrWhiteSpace(email))
            {
                email = form["identifier"].ToString();
            }

            var otpCode = ReadOtpCode(form);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

            var result = await authenticationService.ValidateOtpAsync(email, otpCode, context.RequestAborted);
            if (!result.Succeeded || !result.UserId.HasValue || string.IsNullOrWhiteSpace(result.Email))
            {
                context.Response.Redirect(BuildLoginRedirectTarget(returnUrl, result.FailureMessage, email));
                return;
            }

            Claim[] claims =
            [
                new Claim(ClaimTypes.NameIdentifier, result.UserId.Value.ToString()),
                new Claim(ClaimTypes.Name, result.Email),
                new Claim(ClaimTypes.Email, result.Email),
                new Claim("lms:mfa", "true"),
                new Claim("amr", "otp")
            ];

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            var sessionLifetime = TimeSpan.FromMinutes(
                SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.SessionLifetimeMinutes));
            var issuedAtUtc = DateTimeOffset.UtcNow;
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(
                "auth_time",
                issuedAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = false,
                    IssuedUtc = issuedAtUtc,
                    ExpiresUtc = issuedAtUtc.Add(sessionLifetime)
                });

            if (IsPasskeyCapableRequest(context) &&
                !IsEdgeGatewayReturnUrl(returnUrl) &&
                await passkeyAuthenticationService.ShouldOfferPasskeySetupAsync(
                    result.UserId.Value,
                    context.RequestAborted))
            {
                context.Response.Redirect(BuildPasskeySetupRedirectTarget(returnUrl));
                return;
            }

            context.Response.Redirect(returnUrl);
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost(EdgeGatewayAuthenticationPaths.LoginPost, async (
            HttpContext context,
            ISecurityAuthenticationService authenticationService,
            PasskeyAuthenticationService passkeyAuthenticationService) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString();
            if (string.IsNullOrWhiteSpace(email))
            {
                email = form["identifier"].ToString();
            }

            var otpCode = ReadOtpCode(form);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

            var result = await authenticationService.ValidateOtpAsync(email, otpCode, context.RequestAborted);
            if (!result.Succeeded || !result.UserId.HasValue || string.IsNullOrWhiteSpace(result.Email))
            {
                context.Response.Redirect(BuildLoginRedirectTarget(
                    returnUrl,
                    result.FailureMessage,
                    email,
                    loginPath: EdgeGatewayAuthenticationPaths.Login));
                return;
            }

            Claim[] claims =
            [
                new Claim(ClaimTypes.NameIdentifier, result.UserId.Value.ToString()),
                new Claim(ClaimTypes.Name, result.Email),
                new Claim(ClaimTypes.Email, result.Email),
                new Claim("lms:mfa", "true"),
                new Claim("amr", "otp")
            ];

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            var sessionLifetime = TimeSpan.FromMinutes(
                SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.SessionLifetimeMinutes));
            var issuedAtUtc = DateTimeOffset.UtcNow;
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(
                "auth_time",
                issuedAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = false,
                    IssuedUtc = issuedAtUtc,
                    ExpiresUtc = issuedAtUtc.Add(sessionLifetime)
                });

            if (IsPasskeyCapableRequest(context) &&
                !IsEdgeGatewayReturnUrl(returnUrl) &&
                await passkeyAuthenticationService.ShouldOfferPasskeySetupAsync(
                    result.UserId.Value,
                    context.RequestAborted))
            {
                context.Response.Redirect(BuildPasskeySetupRedirectTarget(returnUrl));
                return;
            }

            context.Response.Redirect(returnUrl);
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost("/auth/recovery", async (
            HttpContext context,
            LocalAccessRecoveryService recoveryService,
            ILogger<Program> logger) =>
        {
            var returnUrl = "/";
            var challengeId = string.Empty;
            var email = string.Empty;

            try
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
                challengeId = form["recovery"].ToString();
                email = form["email"].ToString();
                var recoveryCode = form["recoveryCode"].ToString();

                var result = await recoveryService.RecoverAsync(
                    email,
                    challengeId,
                    recoveryCode,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    context.Response.Redirect(BuildLoginRedirectTarget(
                        returnUrl,
                        result.ErrorMessage,
                        email,
                        challengeId,
                        authenticationMethod: "recovery"));
                    return;
                }

                await SignInLmsUserAsync(context, result.User, "local-recovery");
                context.Response.Redirect(returnUrl);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Local access recovery failed.");
                context.Response.Redirect(BuildLoginRedirectTarget(
                    returnUrl,
                    "Local access recovery failed. Re-run the installer over SSH with sudo to generate a new code.",
                    email,
                    challengeId,
                    authenticationMethod: "recovery"));
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost("/api/email-mfa/login/send", async (
            HttpContext context,
            EmailMfaAuthenticationService emailMfaAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<EmailMfaStartRequest>(
                    cancellationToken: context.RequestAborted) ?? new EmailMfaStartRequest(null, null);
                var result = await emailMfaAuthenticationService.SendLoginChallengeAsync(
                    request.Email ?? string.Empty,
                    BuildAbsoluteRequestOrigin(context),
                    NormalizeReturnUrl(request.ReturnUrl),
                    context.RequestAborted);

                return Results.Json(new { result.Succeeded, result.Message });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Email MFA sign-in request failed.");
                return Results.Json(new
                {
                    succeeded = true,
                    message = "If that LMS account can receive email sign-in, check your inbox for the code or secure login link."
                });
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-start");
        app.MapPost("/api/email-mfa/login/complete", async (
            HttpContext context,
            EmailMfaAuthenticationService emailMfaAuthenticationService,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<EmailMfaCompleteRequest>(
                    cancellationToken: context.RequestAborted) ?? new EmailMfaCompleteRequest(null, null, null);
                var returnUrl = NormalizeReturnUrl(request.ReturnUrl);
                var result = await emailMfaAuthenticationService.ValidateCodeAsync(
                    request.Email ?? string.Empty,
                    request.Code ?? string.Empty,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    return Results.Json(new { succeeded = false, message = result.ErrorMessage });
                }

                await SignInLmsUserAsync(context, result.User, "email");
                var redirectUrl = await ResolvePostMfaRedirectUrlAsync(
                    context,
                    passkeyAuthenticationService,
                    result.User,
                    returnUrl);
                return Results.Json(new { succeeded = true, redirectUrl });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Email MFA code completion failed.");
                return Results.Json(
                    new { succeeded = false, message = "Email sign-in failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapGet("/auth/email-mfa/login", async (
            HttpContext context,
            EmailMfaAuthenticationService emailMfaAuthenticationService,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
                var token = context.Request.Query["token"].ToString();
                var result = await emailMfaAuthenticationService.ValidateTokenAsync(
                    token,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    return Results.Redirect(BuildLoginRedirectTarget(
                        returnUrl,
                        result.ErrorMessage,
                        null,
                        authenticationMethod: "email"));
                }

                await SignInLmsUserAsync(context, result.User, "email-link");
                return Results.Redirect(await ResolvePostMfaRedirectUrlAsync(
                    context,
                    passkeyAuthenticationService,
                    result.User,
                    returnUrl));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Email MFA link completion failed.");
                var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
                return Results.Redirect(BuildLoginRedirectTarget(
                    returnUrl,
                    "Email sign-in failed.",
                    null,
                    authenticationMethod: "email"));
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost(EdgeGatewayAuthenticationPaths.EmailSend, async (
            HttpContext context,
            EmailMfaAuthenticationService emailMfaAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<EmailMfaStartRequest>(
                    cancellationToken: context.RequestAborted) ?? new EmailMfaStartRequest(null, null);
                var result = await emailMfaAuthenticationService.SendLoginChallengeAsync(
                    request.Email ?? string.Empty,
                    BuildAbsoluteRequestOrigin(context),
                    NormalizeReturnUrl(request.ReturnUrl),
                    EdgeGatewayAuthenticationPaths.EmailLink,
                    context.RequestAborted);

                return Results.Json(new { result.Succeeded, result.Message });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Edge Gateway email MFA sign-in request failed.");
                return Results.Json(new
                {
                    succeeded = true,
                    message = "If that LMS account can receive email sign-in, check your inbox for the code or secure login link."
                });
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-start");
        app.MapPost(EdgeGatewayAuthenticationPaths.EmailComplete, async (
            HttpContext context,
            EmailMfaAuthenticationService emailMfaAuthenticationService,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<EmailMfaCompleteRequest>(
                    cancellationToken: context.RequestAborted) ?? new EmailMfaCompleteRequest(null, null, null);
                var returnUrl = NormalizeReturnUrl(request.ReturnUrl);
                var result = await emailMfaAuthenticationService.ValidateCodeAsync(
                    request.Email ?? string.Empty,
                    request.Code ?? string.Empty,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    return Results.Json(new { succeeded = false, message = result.ErrorMessage });
                }

                await SignInLmsUserAsync(context, result.User, "email");
                var redirectUrl = await ResolvePostMfaRedirectUrlAsync(
                    context,
                    passkeyAuthenticationService,
                    result.User,
                    returnUrl);
                return Results.Json(new { succeeded = true, redirectUrl });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Edge Gateway email MFA code completion failed.");
                return Results.Json(
                    new { succeeded = false, message = "Email sign-in failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapGet(EdgeGatewayAuthenticationPaths.EmailLink, async (
            HttpContext context,
            EmailMfaAuthenticationService emailMfaAuthenticationService,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
                var token = context.Request.Query["token"].ToString();
                var result = await emailMfaAuthenticationService.ValidateTokenAsync(
                    token,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    return Results.Redirect(BuildLoginRedirectTarget(
                        returnUrl,
                        result.ErrorMessage,
                        null,
                        loginPath: EdgeGatewayAuthenticationPaths.Login,
                        authenticationMethod: "email"));
                }

                await SignInLmsUserAsync(context, result.User, "email-link");
                return Results.Redirect(await ResolvePostMfaRedirectUrlAsync(
                    context,
                    passkeyAuthenticationService,
                    result.User,
                    returnUrl));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Edge Gateway email MFA link completion failed.");
                var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
                return Results.Redirect(BuildLoginRedirectTarget(
                    returnUrl,
                    "Email sign-in failed.",
                    null,
                    loginPath: EdgeGatewayAuthenticationPaths.Login,
                    authenticationMethod: "email"));
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        }).DisableAntiforgery();
        app.MapPost("/auth/passkeys/verify", async (
            HttpContext context,
            ISecurityAuthenticationService authenticationService,
            ISecurityUserStore securityUserStore) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
            var setupUrl = BuildPasskeySetupRedirectTarget(returnUrl);
            var currentUserIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var currentEmail = context.User.FindFirstValue(ClaimTypes.Email) ??
                               context.User.Identity?.Name ??
                               string.Empty;
            if (context.User.Identity?.IsAuthenticated != true ||
                !Guid.TryParse(currentUserIdValue, out var currentUserId) ||
                string.IsNullOrWhiteSpace(currentEmail))
            {
                return Results.Redirect(BuildLoginRedirectTarget(
                    setupUrl,
                    "Sign in before setting up a passkey.",
                    currentEmail));
            }

            var result = await authenticationService.ValidateOtpAsync(
                currentEmail,
                ReadOtpCode(form),
                context.RequestAborted);
            if (!result.Succeeded ||
                result.UserId != currentUserId)
            {
                return Results.Redirect(BuildPasskeySetupRedirectTarget(
                    returnUrl,
                    result.FailureMessage ?? "The authenticator code was not valid."));
            }

            var user = await securityUserStore.GetAsync(currentUserId, context.RequestAborted);
            if (user is null || !user.IsEnabled)
            {
                return Results.Redirect(BuildPasskeySetupRedirectTarget(
                    returnUrl,
                    "The signed-in LMS account is no longer available."));
            }

            await SignInLmsUserAsync(context, user, "otp");
            return Results.Redirect(setupUrl);
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapGet("/api/passkeys", async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService) =>
        {
            var passkeys = await passkeyAuthenticationService.ListForPrincipalAsync(
                context.User,
                context.RequestAborted);

            return Results.Json(passkeys.Select(passkey => new
            {
                passkey.Id,
                passkey.FriendlyName,
                passkey.CreatedAtUtc,
                passkey.LastUsedAtUtc
            }));
        });
        app.MapDelete("/api/passkeys/{passkeyId:guid}", async (
            HttpContext context,
            Guid passkeyId,
            PasskeyAuthenticationService passkeyAuthenticationService) =>
        {
            var result = await passkeyAuthenticationService.DeleteAsync(
                context.User,
                passkeyId,
                context.RequestAborted);
            return Results.Json(new { result.Succeeded, result.Message });
        });
        app.MapPost("/api/passkeys/enroll/options", async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<PasskeyEnrollmentOptionsRequest>(
                    cancellationToken: context.RequestAborted) ?? new PasskeyEnrollmentOptionsRequest(null, null);
                var localAdministrator = IsTrustedLocalAdministrator(context);
                var currentUserId = TryResolveAuthenticatedUserId(context.User);
                PasskeyOptionsResult result;
                if (request.TargetUserId is { } targetUserId)
                {
                    if (!localAdministrator && currentUserId != targetUserId)
                    {
                        result = PasskeyOptionsResult.Fail("You can only add a passkey to your own LMS account.");
                    }
                    else if (localAdministrator)
                    {
                        result = await passkeyAuthenticationService.BuildAdministratorRegistrationOptionsAsync(
                            targetUserId,
                            request.FriendlyName ?? string.Empty,
                            context.Request,
                            context.RequestAborted);
                    }
                    else
                    {
                        result = await passkeyAuthenticationService.BuildAuthenticatedRegistrationOptionsAsync(
                            context.User,
                            request.FriendlyName ?? string.Empty,
                            context.Request,
                            context.RequestAborted);
                    }
                }
                else
                {
                    result = await passkeyAuthenticationService.BuildAuthenticatedRegistrationOptionsAsync(
                        context.User,
                        request.FriendlyName ?? string.Empty,
                        context.Request,
                        context.RequestAborted);
                }

                return BuildPasskeyOptionsResponse(result);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Passkey MFA enrollment options request failed.");
                return Results.Json(
                    new { succeeded = false, message = "Passkey setup could not start." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery();
        app.MapPost("/api/passkeys/register/complete", async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var (stateId, credentialJson, error) = await ReadPasskeyCeremonyRequestAsync(context);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return Results.BadRequest(new { succeeded = false, message = error });
                }

                var result = IsTrustedLocalAdministrator(context)
                    ? await passkeyAuthenticationService.CompleteAdministratorRegistrationAsync(
                        stateId,
                        credentialJson,
                        context.Request,
                        context.RequestAborted)
                    : await passkeyAuthenticationService.CompleteRegistrationAsync(
                        context.User,
                        stateId,
                        credentialJson,
                        context.Request,
                        context.RequestAborted);
                return Results.Json(new { result.Succeeded, result.Message });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Passkey registration completion request failed.");
                return Results.Json(
                    new { succeeded = false, message = "Passkey setup failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery();
        app.MapPost("/api/passkeys/login/options", async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                _ = await context.Request.ReadFromJsonAsync<PasskeyLoginOptionsRequest>(
                    cancellationToken: context.RequestAborted) ?? new PasskeyLoginOptionsRequest();
                var result = await passkeyAuthenticationService.BuildLoginOptionsAsync(
                    context.Request,
                    context.RequestAborted);

                return BuildPasskeyOptionsResponse(result);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Passkey sign-in options request failed.");
                return Results.Json(
                    new { succeeded = false, message = "Passkey sign-in could not start." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-start");
        app.MapPost("/api/passkeys/login/complete", async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var (stateId, credentialJson, error) = await ReadPasskeyCeremonyRequestAsync(context);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return Results.BadRequest(new { succeeded = false, message = error });
                }

                var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
                var result = await passkeyAuthenticationService.CompleteLoginAsync(
                    stateId,
                    credentialJson,
                    context.Request,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    return Results.Json(new { succeeded = false, message = result.ErrorMessage });
                }

                Claim[] claims =
                [
                    new Claim(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
                    new Claim(ClaimTypes.Name, result.User.Email),
                    new Claim(ClaimTypes.Email, result.User.Email),
                    new Claim("lms:mfa", "true"),
                    new Claim("lms:passkey", "true"),
                    new Claim("amr", "passkey"),
                    new Claim(
                        "auth_time",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
                ];

                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
                var sessionLifetime = TimeSpan.FromMinutes(
                    SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.User.SessionLifetimeMinutes));
                var issuedAtUtc = DateTimeOffset.UtcNow;
                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = false,
                        AllowRefresh = false,
                        IssuedUtc = issuedAtUtc,
                        ExpiresUtc = issuedAtUtc.Add(sessionLifetime)
                    });

                return Results.Json(new { succeeded = true, redirectUrl = returnUrl });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Passkey sign-in completion request failed.");
                return Results.Json(
                    new { succeeded = false, message = "Passkey sign-in failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost(EdgeGatewayAuthenticationPaths.PasskeyOptions, async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                _ = await context.Request.ReadFromJsonAsync<PasskeyLoginOptionsRequest>(
                    cancellationToken: context.RequestAborted) ?? new PasskeyLoginOptionsRequest();
                var result = await passkeyAuthenticationService.BuildLoginOptionsAsync(
                    context.Request,
                    context.RequestAborted);

                return BuildPasskeyOptionsResponse(result);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Edge Gateway passkey sign-in options request failed.");
                return Results.Json(
                    new { succeeded = false, message = "Passkey sign-in could not start." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-start");
        app.MapPost(EdgeGatewayAuthenticationPaths.PasskeyComplete, async (
            HttpContext context,
            PasskeyAuthenticationService passkeyAuthenticationService,
            ILogger<Program> logger) =>
        {
            try
            {
                var (stateId, credentialJson, error) = await ReadPasskeyCeremonyRequestAsync(context);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return Results.BadRequest(new { succeeded = false, message = error });
                }

                var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
                var result = await passkeyAuthenticationService.CompleteLoginAsync(
                    stateId,
                    credentialJson,
                    context.Request,
                    context.RequestAborted);
                if (!result.Succeeded || result.User is null)
                {
                    return Results.Json(new { succeeded = false, message = result.ErrorMessage });
                }

                Claim[] claims =
                [
                    new Claim(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
                    new Claim(ClaimTypes.Name, result.User.Email),
                    new Claim(ClaimTypes.Email, result.User.Email),
                    new Claim("lms:mfa", "true"),
                    new Claim("lms:passkey", "true"),
                    new Claim("amr", "passkey"),
                    new Claim(
                        "auth_time",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
                ];

                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
                var sessionLifetime = TimeSpan.FromMinutes(
                    SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.User.SessionLifetimeMinutes));
                var issuedAtUtc = DateTimeOffset.UtcNow;
                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = false,
                        AllowRefresh = false,
                        IssuedUtc = issuedAtUtc,
                        ExpiresUtc = issuedAtUtc.Add(sessionLifetime)
                    });

                return Results.Json(new { succeeded = true, redirectUrl = returnUrl });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Edge Gateway passkey sign-in completion request failed.");
                return Results.Json(
                    new { succeeded = false, message = "Passkey sign-in failed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
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
        app.MapGet("/internal/file-actions/downloads/{token}/content", async (
            HttpContext context,
            string token,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService) =>
        {
            if (!browserFileTransferService.TryGetDownloadArtifact(token, out var artifact) ||
                !File.Exists(artifact.LocalFilePath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = artifact.ContentType;
            context.Response.ContentLength = artifact.TotalBytes;
            context.Response.Headers["Content-Disposition"] = BuildAttachmentContentDisposition(artifact.DownloadFileName);
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";

            var progress = new ActionProgress<BrowserDownloadProgressResult>(result =>
                fileActionQueueService.ReportBrowserDownloadProgress(
                    result.WorkspaceId,
                    result.JobId,
                    result.ItemId,
                    result.BytesTransferred,
                    result.TotalBytes));

            try
            {
                await browserFileTransferService.StreamDownloadToAsync(
                    token,
                    context.Response.Body,
                    progress,
                    context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        });
        app.MapPost("/internal/file-actions/downloads/{token}/progress", (
            string token,
            BrowserDownloadProgressUpdate request,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService) =>
        {
            if (!browserFileTransferService.TryReportDownloadProgress(token, request.BytesTransferred, out var result))
            {
                return Results.Ok();
            }

            fileActionQueueService.ReportBrowserDownloadProgress(result.WorkspaceId, result.JobId, result.ItemId, result.BytesTransferred, result.TotalBytes);
            return Results.Ok();
        }).DisableAntiforgery();
        app.MapPost("/internal/file-actions/downloads/{token}/complete", async (
            string token,
            BrowserFileTransferService browserFileTransferService,
            FileActionQueueService fileActionQueueService,
            CancellationToken cancellationToken) =>
        {
            if (!browserFileTransferService.TryCompleteDownload(token, out var result))
            {
                return Results.Ok();
            }

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
        app.MapStorageApi();
        app.MapLocalAiPeerApi();
        var componentEndpoint = app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        if (PluginModuleLoader.LoadedAssemblies.Count > 0)
        {
            componentEndpoint.AddAdditionalAssemblies(PluginModuleLoader.LoadedAssemblies.ToArray());
        }

        if (TryHandleConsoleCommand(args, app, launchDirectory))
        {
            return;
        }

        app.AddLocalAiServiceAddressesAsync().GetAwaiter().GetResult();
        app.Run();
    }

    private static bool TryHandlePrivilegedDriveUsageCommand(string[] args)
    {
        if (args.Length != 2 ||
            !args[0].Equals(LocalDriveUsageService.PrivilegedScanCommand, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var snapshot = new LocalDriveUsageService()
                .ScanAsync(args[1])
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(JsonSerializer.Serialize(snapshot));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Drive usage scan failed: {exception.Message}");
            Environment.ExitCode = 1;
        }

        return true;
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

    private static void RegisterStartupConsoleSummary(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var urls = app.Urls
                .OrderBy(static url => url, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (urls.Length == 0)
            {
                Console.WriteLine("Linux Made Sane started. No bound URLs were reported by Kestrel.");
                return;
            }

            Console.WriteLine($"Linux Made Sane listening on: {string.Join(", ", urls)}");
        });
    }

    private static bool TryHandleConsoleCommand(string[] args, WebApplication app, string launchDirectory)
    {
        if (args.Any(argument =>
                argument.Equals("smoke-startup", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--smoke-startup", StringComparison.OrdinalIgnoreCase)))
        {
            SmokeStartupAsync(app.Services).GetAwaiter().GetResult();
            return true;
        }

        if (args.Any(argument =>
                argument.Equals("unlock-security", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--unlock-security", StringComparison.OrdinalIgnoreCase)))
        {
            UnlockSecurityAsync(app.Services).GetAwaiter().GetResult();
            return true;
        }

        if (args.Any(argument =>
                argument.Equals("repair-home-lab-gateways", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--repair-home-lab-gateways", StringComparison.OrdinalIgnoreCase)))
        {
            RepairHomeLabGatewaysAsync(app.Services).GetAwaiter().GetResult();
            return true;
        }

        if (args.Any(argument =>
                argument.Equals("apply-home-lab-storage", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--apply-home-lab-storage", StringComparison.OrdinalIgnoreCase)))
        {
            ApplyHomeLabStorageAsync(app.Services).GetAwaiter().GetResult();
            return true;
        }

        return false;
    }

    private static async Task RepairHomeLabGatewaysAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var homeLabService = scope.ServiceProvider.GetRequiredService<IHomeLabService>();
        var workspace = await homeLabService.GetWorkspaceAsync();
        var gateways = workspace.Installations
            .Where(installation => installation.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (gateways.Length == 0)
        {
            Console.WriteLine("No Home Lab VPN Gateways are installed.");
            return;
        }

        foreach (var gateway in gateways)
        {
            var result = await homeLabService.ExecuteAsync(gateway.Id, HomeLabLifecycleAction.Repair);
            Console.WriteLine($"{gateway.DisplayName}: {result.Summary} {result.Detail}");
            if (!result.Succeeded)
            {
                Environment.ExitCode = 1;
            }
        }
    }

    private static async Task ApplyHomeLabStorageAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var homeLabService = scope.ServiceProvider.GetRequiredService<IHomeLabService>();
        var result = await homeLabService.ApplyStandardStorageAsync();
        Console.WriteLine($"{result.Summary} {result.Detail}");
        if (!result.Succeeded)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task SmokeStartupAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        ComponentInjectionValidator.Validate(scope.ServiceProvider, typeof(App).Assembly);
        LinuxMadeSane.Infrastructure.Services.SshForwards.SshForwardProcessFactory.ValidateEmbeddedResources();
        var dbContext = scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
        var dataSource = dbContext.Database.GetDbConnection().DataSource;

        Console.WriteLine("Linux Made Sane startup smoke passed.");
        Console.WriteLine($"Version: {ResolveProductVersion()}");
        Console.WriteLine($"Database: {dataSource}");
        Console.WriteLine($"Managed hosts: {await dbContext.ManagedHosts.CountAsync()}");
    }

    private static string ResolveProductVersion()
        => LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly);

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
        Console.WriteLine("Open http://127.0.0.1:5080 on the server console, or use SSH port forwarding, then fix the LMS Accounts rules.");
    }

    private static bool IsAlwaysAnonymousAllowedPath(PathString path)
    {
        if (!path.HasValue)
        {
            return true;
        }

        if (path.StartsWithSegments("/healthz") ||
            path.StartsWithSegments("/v1") ||
            path.StartsWithSegments("/setup") ||
            path.StartsWithSegments("/auth/setup") ||
            path.StartsWithSegments("/desktop-assistant/launch") ||
            path.StartsWithSegments("/api/desktop-assistant/native") ||
            path.StartsWithSegments(EdgeGatewayAuthenticationPaths.ApiPrefix) ||
            path.StartsWithSegments("/edge-auth/check") ||
            path.Value?.Equals("/edge-auth/on-demand", StringComparison.OrdinalIgnoreCase) == true ||
            path.StartsWithSegments("/api/passkeys/login") ||
            path.StartsWithSegments("/api/email-mfa/login") ||
            path.StartsWithSegments("/internal/lms-tunnel") ||
            path.StartsWithSegments("/api/integrations/media-library") ||
            path.StartsWithSegments("/internal/scheduler") ||
            path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/_content") ||
            path.StartsWithSegments("/scripts") ||
            path.StartsWithSegments("/styles") ||
            path.StartsWithSegments("/lib") ||
            path.StartsWithSegments("/Error"))
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

    private static async Task<TrustedNetworkAccessResult?> TryEvaluateNoAccessCloudflareLocalExposureAsync(
        HttpContext context,
        ITrustedNetworkAccessService trustedNetworkAccessService)
    {
        if (context.Items[OriginalConnectionRemoteIpAddressItemKey] is not IPAddress originalRemoteIpAddress ||
            !IsLoopbackRequest(originalRemoteIpAddress))
        {
            return null;
        }

        var requestHost = context.Request.Host.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return null;
        }

        var exposureStore = context.RequestServices.GetRequiredService<ICloudflareExposureStore>();
        var exposure = await exposureStore.GetConfigByHostnameAsync(
            AiLocalMachine.ManagedHostId,
            requestHost,
            context.RequestAborted);

        if (exposure is null ||
            exposure.DisabledAtUtc.HasValue ||
            exposure.AccessMode != ExposedServiceAccessMode.NoAccessProtection ||
            !IsLoopbackServiceTarget(exposure.LocalServiceUrl))
        {
            return null;
        }

        return await trustedNetworkAccessService.EvaluateAsync(
            originalRemoteIpAddress,
            context.Request.Host.Host,
            context.RequestAborted);
    }

    private static bool IsLoopbackServiceTarget(string localServiceUrl)
    {
        if (!Uri.TryCreate(localServiceUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsMediaLibraryApiPath(PathString path) =>
        path.StartsWithSegments("/api/integrations/media-library");

    private static bool IsEdgeAuthCheckPath(PathString path) =>
        path.StartsWithSegments("/edge-auth/check");

    private static bool IsInitialSetupPath(PathString path) =>
        path.Value?.Equals("/InitialSetup", StringComparison.OrdinalIgnoreCase) == true ||
        path.Value?.Equals("/initial-setup", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSetupPath(PathString path) =>
        path.Value?.Equals("/setup", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsAuthenticationEntryPath(PathString path) =>
        IsSetupPath(path) ||
        IsInitialSetupPath(path) ||
        path.StartsWithSegments(EdgeGatewayAuthenticationPaths.Login) ||
        path.StartsWithSegments(EdgeGatewayAuthenticationPaths.ApiPrefix) ||
        path.StartsWithSegments("/login") ||
        path.StartsWithSegments("/auth");

    private static string BuildPasskeySetupRedirectTarget(string returnUrl, string? errorMessage = null)
    {
        var target = $"/auth/setup-passkey?returnUrl={Uri.EscapeDataString(NormalizeReturnUrl(returnUrl))}";
        return string.IsNullOrWhiteSpace(errorMessage)
            ? target
            : $"{target}&error={Uri.EscapeDataString(errorMessage)}";
    }

    private static bool IsEdgeGatewayReturnUrl(string? returnUrl) =>
        NormalizeReturnUrl(returnUrl).StartsWith("/edge-auth/return", StringComparison.OrdinalIgnoreCase);

    private static string BuildTemporaryIpApprovalHtml(EdgeGatewayTemporaryIpApprovalCompletionViewModel result,
        string? blockToken = null, string? antiforgeryFieldName = null, string? antiforgeryToken = null)
    {
        var blockView = result.IsBlocked || blockToken is not null;
        var statusColor = result.Success && !blockView ? "#0f7b57" : "#a33d2f";
        var statusText = result.IsBlocked ? "Blocked" : blockToken is not null ? "Not blocked yet" : result.Success ? "Approved" : "Not approved";
        var eyebrow = result.Success ? "Linux Made Sane - Edge Gateway" : "Approval unavailable";
        var title = WebUtility.HtmlEncode(result.Title);
        var message = WebUtility.HtmlEncode(result.Message);
        var routeName = WebUtility.HtmlEncode(result.RouteName);
        var sourceIp = WebUtility.HtmlEncode(result.SourceIp);
        var country = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(result.CountryCode) ? "Unknown" : result.CountryCode);
        var approvedUrl = WebUtility.HtmlEncode(result.ApprovedUrl);
        var idleExpiry = WebUtility.HtmlEncode(FormatApprovalTime(result.IdleExpiresAtUtc));
        var maxExpiry = WebUtility.HtmlEncode(FormatApprovalTime(result.ExpiresAtUtc));
        var action = result.Success && !string.IsNullOrWhiteSpace(result.ApprovedUrl)
            ? $"""<a class="button" href="{approvedUrl}">Open approved app</a>"""
            : """<a class="button secondary" href="/">Return to LMS</a>""";
        if (blockToken is not null && result.Success && !string.IsNullOrWhiteSpace(antiforgeryToken))
            action = $"""
                <form method="post" action="/edge-auth/block-ip">
                  <input type="hidden" name="token" value="{WebUtility.HtmlEncode(blockToken)}">
                  <input type="hidden" name="{WebUtility.HtmlEncode(antiforgeryFieldName)}" value="{WebUtility.HtmlEncode(antiforgeryToken)}">
                  <button class="button" type="submit">Block this IP for this app</button>
                </form>
                <p>You can unblock it later in LMS. Closing this page makes no change.</p>
                """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="referrer" content="no-referrer">
              <title>{{title}}</title>
              <style>
                :root { color-scheme: light; }
                body { margin: 0; background: #edf3f8; color: #142033; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
                main { min-height: 100vh; display: grid; place-items: center; padding: 24px; }
                article { width: min(620px, 100%); background: #fff; border: 1px solid #dce7f3; border-radius: 22px; box-shadow: 0 28px 80px rgba(20,32,51,.16); overflow: hidden; }
                header { padding: 24px 28px; background: #102033; color: #fff; }
                .eyebrow { color: #9fd5ff; font-size: 12px; font-weight: 900; letter-spacing: .08em; text-transform: uppercase; }
                h1 { margin: 8px 0 0; font-size: clamp(24px, 4vw, 34px); line-height: 1.05; }
                .body { padding: 26px 28px 30px; }
                .status { display: inline-flex; align-items: center; gap: 8px; color: {{statusColor}}; font-weight: 900; }
                .dot { width: 10px; height: 10px; border-radius: 99px; background: {{statusColor}}; }
                p { line-height: 1.55; }
                .details { display: grid; gap: 10px; margin: 20px 0; }
                .detail { border: 1px solid #e3edf7; border-radius: 14px; background: #f7f9fc; padding: 12px 14px; }
                .detail span { display: block; color: #607089; font-size: 11px; font-weight: 900; letter-spacing: .08em; text-transform: uppercase; }
                .detail strong { display: block; margin-top: 4px; word-break: break-word; }
                .button { display: inline-block; margin-top: 8px; padding: 13px 18px; border-radius: 12px; background: {{statusColor}}; color: #fff; font:inherit; font-weight: 900; text-decoration: none; border:0; cursor:pointer; }
                .button.secondary { background: #526070; }
              </style>
            </head>
            <body>
              <main>
                <article>
                  <header>
                    <div class="eyebrow">{{WebUtility.HtmlEncode(eyebrow)}}</div>
                    <h1>{{title}}</h1>
                  </header>
                  <div class="body">
                    <div class="status"><span class="dot" aria-hidden="true"></span><span>{{statusText}}</span></div>
                    <p>{{message}}</p>
                    {{(result.Success ? $"""
                    <div class="details">
                      {BuildApprovalDetail("Route", routeName)}
                      {BuildApprovalDetail("Source IP", sourceIp)}
                      {BuildApprovalDetail("Country", country)}
                      {(blockView ? string.Empty : BuildApprovalDetail("Idle expiry", idleExpiry))}
                      {(blockView ? string.Empty : BuildApprovalDetail("Maximum expiry", maxExpiry))}
                    </div>
                    """ : string.Empty)}}
                    {{action}}
                  </div>
                </article>
              </main>
            </body>
            </html>
            """;
    }

    private static string BuildOnDemandAppLaunchProgressHtml(Guid jobId)
    {
        var statusUrl = WebUtility.HtmlEncode($"/on-demand-apps/launch/{jobId:D}/status");
        return $$$"""
                 <!doctype html>
                 <html lang="en">
                 <head>
                   <meta charset="utf-8">
                   <meta name="viewport" content="width=device-width,initial-scale=1">
                   <meta name="referrer" content="no-referrer">
                   <title>Connecting app | Linux Made Sane</title>
                   <style>
                     :root{color-scheme:dark}*{box-sizing:border-box}body{background:radial-gradient(circle at top,#1d2a44 0,#101827 42rem);color:#e8eef8;font:15px system-ui,sans-serif;display:grid;min-height:100vh;margin:0;place-items:center;padding:1.25rem}main{background:rgba(15,23,42,.92);border:1px solid #334155;border-radius:1rem;box-shadow:0 24px 70px rgba(0,0,0,.4);max-width:42rem;padding:2rem;width:100%}.heading{align-items:center;display:flex;gap:1rem}.hero-spinner,.step-spinner{animation:spin .85s linear infinite;border:3px solid #334155;border-radius:50%;border-top-color:#60a5fa;display:inline-block;flex:0 0 auto;height:2rem;width:2rem}.step-spinner{border-width:2px;height:1rem;width:1rem}h1{font-size:1.5rem;margin:0}#summary{color:#a9b8ce;margin:.45rem 0 1.5rem}.log{display:grid;gap:.65rem;list-style:none;margin:0;padding:0}.log li{align-items:start;background:#111c30;border:1px solid #28364e;border-radius:.65rem;display:grid;gap:.75rem;grid-template-columns:1.1rem 1fr auto;padding:.75rem .85rem}.log li.done .icon{color:#4ade80}.log li.failed{border-color:#7f1d1d}.log li.failed .icon{color:#f87171}.icon{color:#7dd3fc;font-weight:800;line-height:1.1}.message{line-height:1.35}.time{color:#718096;font-size:.75rem;white-space:nowrap}.actions{display:none;margin-top:1.25rem}.actions.show{display:flex;gap:.75rem}.actions a,.actions button{background:#2563eb;border:0;border-radius:.55rem;color:white;cursor:pointer;font:inherit;font-weight:700;padding:.7rem 1rem;text-decoration:none}.actions button{background:#334155}@keyframes spin{to{transform:rotate(360deg)}}
                   </style>
                 </head>
                 <body>
                   <main id="launch" data-status-url="{{{statusUrl}}}">
                     <div class="heading"><span class="hero-spinner" aria-hidden="true"></span><div><h1>Opening your app</h1><p id="summary" role="status" aria-live="polite">LMS is preparing a secure temporary connection.</p></div></div>
                     <ol id="log" class="log" aria-live="polite"></ol>
                     <div id="actions" class="actions"><a href="/edge-gateway?tab=on-demand-apps">Return to apps</a><button id="close" type="button">Close window</button></div>
                   </main>
                   <script>
                     (() => {
                       const root = document.getElementById('launch');
                       const log = document.getElementById('log');
                       const summary = document.getElementById('summary');
                       const spinner = document.querySelector('.hero-spinner');
                       const actions = document.getElementById('actions');
                       document.getElementById('close').addEventListener('click', () => window.close());
                       let finished = false;

                       const stopWithError = message => {
                         finished = true;
                         spinner.style.display = 'none';
                         summary.textContent = message;
                         actions.classList.add('show');
                       };

                       const render = data => {
                         log.replaceChildren();
                         const entries = Array.isArray(data.entries) ? data.entries : [];
                         entries.forEach((entry, index) => {
                           const item = document.createElement('li');
                           const isCurrent = data.state === 'running' && index === entries.length - 1;
                           item.className = data.state === 'failed' && index === entries.length - 1 ? 'failed' : (isCurrent ? 'current' : 'done');
                           const icon = document.createElement('span');
                           icon.className = 'icon';
                           if (isCurrent) {
                             icon.className = 'step-spinner';
                           } else {
                             icon.textContent = item.className === 'failed' ? '×' : '✓';
                           }
                           const message = document.createElement('span');
                           message.className = 'message';
                           message.textContent = entry.message;
                           const time = document.createElement('time');
                           time.className = 'time';
                           time.textContent = new Date(entry.timestampUtc).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'});
                           item.append(icon, message, time);
                           log.append(item);
                         });
                       };

                       const poll = async () => {
                         if (finished) return;
                         try {
                           const response = await fetch(root.dataset.statusUrl, {credentials:'same-origin',cache:'no-store'});
                           if (response.status === 401 || response.status === 403) {
                             stopWithError('Your LMS sign-in ended before the app connection was ready. Return to LMS and sign in again.');
                             return;
                           }
                           if (response.status === 404) {
                             stopWithError('This app launch is no longer available. Return to On-Demand Apps and open it again.');
                             return;
                           }
                           if (!response.ok) throw new Error(`HTTP ${response.status}`);
                           const data = await response.json();
                           render(data);
                           if (data.state === 'succeeded') {
                             finished = true;
                             summary.textContent = 'Connection ready. Passing your LMS sign-in to the app.';
                             setTimeout(() => window.location.replace(data.completionUrl), 350);
                             return;
                           }
                           if (data.state === 'failed') {
                             stopWithError(data.error || 'The app connection could not be created.');
                             return;
                           }
                           summary.textContent = entriesSummary(data.entries);
                         } catch {
                           summary.textContent = 'Reconnecting to LMS while the app preparation continues…';
                         }
                         setTimeout(poll, 600);
                       };

                       const entriesSummary = entries => Array.isArray(entries) && entries.length
                         ? entries[entries.length - 1].message
                         : 'LMS is preparing a secure temporary connection.';
                       poll();
                     })();
                   </script>
                 </body>
                 </html>
                 """;
    }

    private static string BuildOnDemandAppLaunchHtml(OnDemandAppLaunch launch, string ticket)
    {
        var action = WebUtility.HtmlEncode($"https://{launch.Hostname}/edge-auth/on-demand");
        var encodedTicket = WebUtility.HtmlEncode(ticket);
        var appUrl = WebUtility.HtmlEncode(launch.Url);
        return $$$"""
                 <!doctype html>
                 <html lang="en">
                 <head>
                   <meta charset="utf-8">
                   <meta name="viewport" content="width=device-width,initial-scale=1">
                   <meta name="referrer" content="no-referrer">
                   <title>Opening app | Linux Made Sane</title>
                   <style>body{background:#101827;color:#e8eef8;font:16px system-ui,sans-serif;display:grid;min-height:100vh;margin:0;place-items:center}main{max-width:34rem;padding:2rem;text-align:center}.spinner{animation:spin .85s linear infinite;border:3px solid #334155;border-radius:50%;border-top-color:#60a5fa;display:inline-block;height:2rem;width:2rem}@keyframes spin{to{transform:rotate(360deg)}}button{background:#3b82f6;border:0;border-radius:.5rem;color:#fff;font:inherit;font-weight:700;padding:.75rem 1rem}</style>
                 </head>
                 <body>
                   <main>
                     <span class="spinner" aria-hidden="true"></span>
                     <h1>Connection ready</h1>
                     <p>Passing your LMS sign-in to {{{appUrl}}}.</p>
                     <form method="post" action="{{{action}}}">
                       <input type="hidden" name="ticket" value="{{{encodedTicket}}}">
                       <noscript><button type="submit">Continue</button></noscript>
                     </form>
                   </main>
                   <script>document.forms[0].submit();</script>
                 </body>
                 </html>
                 """;
    }

    private static string BuildOnDemandAppLaunchFailureHtml(string message)
    {
        var encodedMessage = WebUtility.HtmlEncode(message);
        return $$"""
                 <!doctype html>
                 <html lang="en">
                 <head>
                   <meta charset="utf-8">
                   <meta name="viewport" content="width=device-width,initial-scale=1">
                   <title>App unavailable | Linux Made Sane</title>
                   <style>body{background:#101827;color:#e8eef8;font:16px system-ui,sans-serif;display:grid;min-height:100vh;margin:0;place-items:center}main{max-width:38rem;padding:2rem}button{background:#3b82f6;border:0;border-radius:.5rem;color:#fff;font:inherit;font-weight:700;padding:.75rem 1rem}</style>
                 </head>
                 <body><main><h1>App could not be opened</h1><p>{{encodedMessage}}</p><button type="button" onclick="window.close()">Close</button></main></body>
                 </html>
                 """;
    }

    private static string BuildApprovalDetail(string label, string value) =>
        $"""<div class="detail"><span>{WebUtility.HtmlEncode(label)}</span><strong>{value}</strong></div>""";

    private static string FormatApprovalTime(DateTimeOffset? value) =>
        value.HasValue
            ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)
            : "Unknown";

    private static IResult BuildPasskeyOptionsResponse(PasskeyOptionsResult result)
    {
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StateId) || string.IsNullOrWhiteSpace(result.OptionsJson))
        {
            return Results.Json(new { succeeded = false, message = result.ErrorMessage });
        }

        return Results.Content(
            $$"""{"succeeded":true,"stateId":"{{result.StateId}}","options":{{result.OptionsJson}}}""",
            "application/json",
            Encoding.UTF8);
    }

    private static async Task<(string StateId, string CredentialJson, string? Error)> ReadPasskeyCeremonyRequestAsync(
        HttpContext context)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted);
            var root = document.RootElement;
            if (!root.TryGetProperty("stateId", out var stateIdElement) ||
                string.IsNullOrWhiteSpace(stateIdElement.GetString()))
            {
                return (string.Empty, string.Empty, "The passkey state was missing.");
            }

            if (!root.TryGetProperty("credential", out var credentialElement))
            {
                return (string.Empty, string.Empty, "The passkey credential response was missing.");
            }

            return (stateIdElement.GetString()!, credentialElement.GetRawText(), null);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, "The passkey request was not valid JSON.");
        }
    }

    private static bool IsLoopbackRequest(IPAddress? remoteIpAddress) =>
        remoteIpAddress is not null && IPAddress.IsLoopback(remoteIpAddress);

    private static bool IsTrustedLocalAdministrator(HttpContext context) =>
        context.User.Identity?.IsAuthenticated != true &&
        context.Items.TryGetValue("LmsTrustedNetworkAccess", out var value) &&
        value is TrustedNetworkAccessResult { IsTrusted: true };

    private static Guid? TryResolveAuthenticatedUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId) ? userId : null;
    }

    private static bool TokenMatches(string configuredToken, string suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken.Trim());
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken.Trim());
        return configuredBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes);
    }

    private static bool TryAuthorizeDesktopAssistantNativeRequest(
        HttpContext context,
        IDesktopAssistantLaunchTicketStore launchTicketStore,
        out IResult rejection)
    {
        if (!IsOriginalLoopbackRequest(context) ||
            !IsOriginalLoopbackRequestHost(context))
        {
            rejection = Results.NotFound();
            return false;
        }

        var ticket = context.Request.Headers.TryGetValue("X-LMS-Desktop-Ticket", out var values)
            ? values.ToString()
            : string.Empty;
        if (!launchTicketStore.TryValidate(ticket))
        {
            rejection = Results.Unauthorized();
            return false;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        rejection = Results.Empty;
        return true;
    }

    private static DesktopAssistantNativeWorkspaceResponse MapDesktopAssistantNativeWorkspace(
        DesktopAssistantChatWorkspaceViewModel workspace,
        AiChatThreadEditorContextViewModel providerContext,
        DesktopAssistantNativeTheme theme)
    {
        var runtimeImplementedProviders = providerContext.SupportedProviders
            .Where(provider => provider.IsRuntimeImplemented)
            .Select(provider => provider.ProviderType)
            .ToHashSet();
        var providers = providerContext.ConfiguredProviders
            .Where(provider =>
                provider.IsEnabled &&
                runtimeImplementedProviders.Contains(provider.ProviderType))
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new DesktopAssistantNativeProvider(
                provider.ProviderKey,
                provider.DisplayName,
                provider.IsDefault,
                provider.DefaultModelId))
            .ToArray();
        var models = providerContext.Models
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(model => new DesktopAssistantNativeModel(
                model.ProviderKey,
                model.ModelId,
                model.DisplayName))
            .ToArray();

        return new DesktopAssistantNativeWorkspaceResponse(
            workspace.Sessions.Select(session => new DesktopAssistantNativeChatSession(
                session.Id,
                session.Title,
                session.ProviderKey,
                session.ProviderLabel,
                session.ModelId,
                session.MessageCount,
                session.UpdatedAtUtc)).ToArray(),
            workspace.ActiveSessionId,
            workspace.Messages
                .Where(message => message.Role is AiChatMessageRole.User or AiChatMessageRole.Assistant)
                .Select(message => new DesktopAssistantNativeChatMessage(
                    message.Id,
                    message.Role.ToString().ToLowerInvariant(),
                    message.Content,
                    message.CreatedAtUtc))
                .ToArray(),
            workspace.IsReady,
            workspace.HasProvider,
            workspace.ActiveProviderKey,
            workspace.ProviderLabel,
            workspace.ModelId,
            workspace.StatusSummary,
            providers,
            models,
            theme,
            workspace.ProposedFix is null
                ? null
                : new DesktopAssistantNativeProposedFix(
                    workspace.ProposedFix.Kind,
                    workspace.ProposedFix.Arguments,
                    workspace.ProposedFix.Title,
                    workspace.ProposedFix.Description));
    }

    private static bool IsOriginalLoopbackRequest(HttpContext context) =>
        context.Items[OriginalConnectionRemoteIpAddressItemKey] is IPAddress originalRemoteIpAddress
            ? IsLoopbackRequest(originalRemoteIpAddress)
            : IsLoopbackRequest(context.Connection.RemoteIpAddress);

    private static bool IsOriginalLoopbackRequestHost(HttpContext context) =>
        context.Items[OriginalRequestHostItemKey] is HostString originalHost
            ? IsLoopbackRequestHost(originalHost)
            : IsLoopbackRequestHost(context.Request.Host);

    private static bool ShouldApplyHttpsRedirection(
        HttpContext context,
        bool isDevelopment,
        bool forceHttpsRedirection)
    {
        if (context.Request.IsHttps ||
            IsEdgeAuthCheckPath(context.Request.Path))
        {
            return false;
        }

        if (forceHttpsRedirection)
        {
            return true;
        }

        if (IsMediaLibraryApiPath(context.Request.Path))
        {
            return false;
        }

        return isDevelopment && IsLoopbackRequestHost(context.Request.Host);
    }

    private static bool IsHttpsRedirectionForced(IConfiguration configuration)
    {
        var value = configuration["Server:EnableHttpsRedirection"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRateLimitPartitionKey(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null)
        {
            return "unknown";
        }

        return (remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress).ToString();
    }

    private static bool IsLoopbackRequestHost(HostString host)
    {
        var value = host.Host.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        value = value.TrimStart('[').TrimEnd(']');
        return IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }

    private static string BuildLoginRedirectTarget(PathString path, string? queryString) =>
        BuildLoginRedirectTarget(NormalizeReturnUrl($"{path}{queryString}"), null, null);

    private static string BuildLoginRedirectTarget(
        string returnUrl,
        string? errorMessage,
        string? email,
        string? recovery = null,
        string loginPath = "/login",
        string? authenticationMethod = null)
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

        if (!string.IsNullOrWhiteSpace(recovery))
        {
            queryParts.Add($"recovery={Uri.EscapeDataString(recovery.Trim())}");
        }

        var normalizedAuthenticationMethod = authenticationMethod?.Trim().ToLowerInvariant();
        if (normalizedAuthenticationMethod is "passkey" or "authenticator" or "email" or "recovery")
        {
            queryParts.Add($"method={Uri.EscapeDataString(normalizedAuthenticationMethod)}");
        }

        return $"{NormalizeLocalRedirectPath(loginPath)}?{string.Join("&", queryParts)}";
    }

    private static string NormalizeLocalRedirectPath(string path)
    {
        var trimmed = path.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal) &&
               !trimmed.StartsWith("//", StringComparison.Ordinal) &&
               !trimmed.StartsWith("/\\", StringComparison.Ordinal)
            ? trimmed
            : "/login";
    }

    private static string BuildInitialSetupRedirectTarget(
        string returnUrl,
        string? errorMessage = null,
        string? email = null,
        string? linuxUsername = null)
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

        if (!string.IsNullOrWhiteSpace(linuxUsername))
        {
            queryParts.Add($"linuxUsername={Uri.EscapeDataString(linuxUsername.Trim())}");
        }

        return $"/setup?{string.Join("&", queryParts)}";
    }

    private static async Task<bool> HasTemporaryRecoveryAccessAsync(HttpContext context) =>
        context.RequestServices.GetRequiredService<TemporarySetupAuthorizationService>().IsAuthorized(context.Request) &&
        await context.RequestServices.GetRequiredService<LocalAccessRecoveryService>()
            .HasActiveTemporarySetupAsync(context.RequestAborted);

    private static string BuildAbsoluteLoginUrl(HttpContext context, string? email)
    {
        var builder = new StringBuilder();
        builder.Append(context.Request.Scheme);
        builder.Append("://");
        builder.Append(context.Request.Host.ToUriComponent());
        builder.Append("/login");

        if (!string.IsNullOrWhiteSpace(email))
        {
            builder.Append("?email=");
            builder.Append(Uri.EscapeDataString(email.Trim()));
        }

        return builder.ToString();
    }

    private static string BuildAbsoluteRequestOrigin(HttpContext context)
    {
        var builder = new StringBuilder();
        builder.Append(context.Request.Scheme);
        builder.Append("://");
        builder.Append(context.Request.Host.ToUriComponent());
        return builder.ToString();
    }

    private static string ReadOtpCode(IFormCollection form)
    {
        var directCode = NormalizeOtpCode(form["otpCode"].ToString());
        if (directCode.Length == 6)
        {
            return directCode;
        }

        var digitValues = form["otpDigit"];
        if (digitValues.Count == 0)
        {
            return directCode;
        }

        var builder = new StringBuilder(6);
        foreach (var value in digitValues)
        {
            foreach (var character in value ?? string.Empty)
            {
                if (!char.IsDigit(character))
                {
                    continue;
                }

                builder.Append(character);
                if (builder.Length == 6)
                {
                    return builder.ToString();
                }
            }
        }

        return builder.ToString();
    }

    private static string NormalizeOtpCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(6);
        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                continue;
            }

            builder.Append(character);
            if (builder.Length == 6)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static async Task SignInLmsUserAsync(
        HttpContext context,
        SecurityUser user,
        string authenticationMethod)
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("lms:mfa", "true"),
            new Claim("amr", authenticationMethod)
        ];

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var sessionLifetime = TimeSpan.FromMinutes(
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes));
        var issuedAtUtc = DateTimeOffset.UtcNow;
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(
            "auth_time",
            issuedAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                IssuedUtc = issuedAtUtc,
                ExpiresUtc = issuedAtUtc.Add(sessionLifetime)
            });
    }

    private static async Task<string> ResolvePostMfaRedirectUrlAsync(
        HttpContext context,
        PasskeyAuthenticationService passkeyAuthenticationService,
        SecurityUser user,
        string returnUrl)
    {
        var normalizedReturnUrl = NormalizeReturnUrl(returnUrl);
        if (IsPasskeyCapableRequest(context) &&
            !IsEdgeGatewayReturnUrl(normalizedReturnUrl) &&
            await passkeyAuthenticationService.ShouldOfferPasskeySetupAsync(
                user.Id,
                context.RequestAborted))
        {
            return BuildPasskeySetupRedirectTarget(normalizedReturnUrl);
        }

        return normalizedReturnUrl;
    }

    private static bool IsPasskeyCapableRequest(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ValidateRemoteSessionAsync(CookieValidatePrincipalContext context)
    {
        var userIdValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            await RejectRemoteSessionAsync(context);
            return;
        }

        var userStore = context.HttpContext.RequestServices.GetRequiredService<ISecurityUserStore>();
        var user = await userStore.GetAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || !user.IsEnabled)
        {
            await RejectRemoteSessionAsync(context);
            return;
        }

        if (context.Properties.IssuedUtc is not DateTimeOffset issuedAtUtc)
        {
            await RejectRemoteSessionAsync(context);
            return;
        }

        var lifetime = TimeSpan.FromMinutes(
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes));
        if (DateTimeOffset.UtcNow >= issuedAtUtc.Add(lifetime))
        {
            await RejectRemoteSessionAsync(context);
            return;
        }

        context.ShouldRenew = false;
    }

    private static async Task RejectRemoteSessionAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    internal static async Task RespondToDeniedNetworkRequestAsync(
        HttpContext context,
        TrustedNetworkAccessResult accessResult)
    {
        if (accessResult.DeniedResponseMode == NetworkAccessDeniedResponseMode.EmptyNotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentLength = 0;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Redirect("/access-denied");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("No access from this network interface.");
    }

    private static string BuildAttachmentContentDisposition(string fileName)
    {
        var safeFileName = string.IsNullOrWhiteSpace(fileName)
            ? "download.bin"
            : fileName.Trim();
        var fallbackFileName = BuildAsciiFileNameFallback(safeFileName);

        return $"attachment; filename=\"{EscapeHeaderQuotedString(fallbackFileName)}\"; filename*=UTF-8''{Uri.EscapeDataString(safeFileName)}";
    }

    private static string BuildAsciiFileNameFallback(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            builder.Append(character is >= ' ' and <= '~' and not '"' and not '\\'
                ? character
                : '_');
        }

        var fallback = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(fallback) ? "download.bin" : fallback;
    }

    private static string EscapeHeaderQuotedString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record PasskeyEnrollmentOptionsRequest(string? FriendlyName, Guid? TargetUserId);

    private sealed record PasskeyLoginOptionsRequest;

    private sealed record EmailMfaStartRequest(string? Email, string? ReturnUrl);

    private sealed record EmailMfaCompleteRequest(string? Email, string? Code, string? ReturnUrl);

    private sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
