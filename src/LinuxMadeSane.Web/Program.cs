// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using LinuxMadeSane.Application;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.Scheduling;
using LinuxMadeSane.Core.Versioning;
using LinuxMadeSane.Infrastructure;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Web.Components;
using LinuxMadeSane.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace LinuxMadeSane.Web;

public class Program
{
    private const string OriginalConnectionRemoteIpAddressItemKey = "LmsOriginalConnectionRemoteIpAddress";

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
        builder.Services.AddMemoryCache();
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
        builder.Services.AddSingleton<TerminalWorkspaceRegistry>();
        builder.Services.AddSingleton<FileBrowserWorkspaceRegistry>();
        builder.Services.AddSingleton<FileActionQueueService>();
        builder.Services.AddSingleton<BrowserFileTransferService>();
        builder.Services.AddScoped<ShareMountsWorkspaceService>();
        builder.Services.AddSingleton<MediaLibrarySignedUrlService>();
        builder.Services.AddSingleton<RemoteLmsTunnelAccessService>();
        builder.Services.AddSingleton<RemoteLmsRelayCaddyService>();
        builder.Services.AddSingleton<RemoteLmsSshTunnelService>();
        builder.Services.AddScoped<LocalInstanceIdentityService>();
        builder.Services.AddScoped<PasskeyAuthenticationService>();
        builder.Services.Configure<ApplicationUpdateOptions>(builder.Configuration.GetSection("ApplicationUpdates"));
        builder.Services.AddHttpClient("ApplicationUpdates");
        builder.Services.AddSingleton(provider =>
            new ApplicationUpdateService(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("ApplicationUpdates"),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ApplicationUpdateOptions>>(),
                provider.GetRequiredService<ILogger<ApplicationUpdateService>>()));
        builder.Services.AddHttpClient<LmsHostUpdateAvailabilityService>()
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
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

        if (TryHandleConsoleCommand(args, app.Services))
        {
            return;
        }

        RegisterStartupConsoleSummary(app);

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.Use(async (context, next) =>
        {
            context.Items[OriginalConnectionRemoteIpAddressItemKey] = context.Connection.RemoteIpAddress;
            await next();
        });
        app.UseForwardedHeaders();
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
            if (IsAlwaysAnonymousAllowedPath(context.Request.Path))
            {
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
                if (await ShouldRedirectToInitialSetupAsync(context))
                {
                    context.Response.Redirect(BuildInitialSetupRedirectTarget(
                        NormalizeReturnUrl($"{context.Request.Path}{context.Request.QueryString}")));
                    return;
                }

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
                if (await ShouldRedirectToInitialSetupAsync(context))
                {
                    context.Response.Redirect(BuildInitialSetupRedirectTarget(
                        NormalizeReturnUrl($"{context.Request.Path}{context.Request.QueryString}")));
                    return;
                }

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

        app.UseRateLimiter();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            product = "linux-made-sane",
            name = "Linux Made Sane",
            version = ResolveProductVersion()
        }));
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
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Expires = session.ExpiresAtUtc
                });

            return Results.Redirect(session.ReturnUrl);
        }).DisableAntiforgery();
        app.MapGet("/edge-auth/check", async Task (
            HttpContext context,
            IEdgeGatewayService edgeGatewayService) =>
        {
            var result = await edgeGatewayService.EvaluateAuthAsync(
                new EdgeGatewayAuthCheckContext(
                    context.Request.Headers["X-Forwarded-Host"].ToString(),
                    context.Request.Headers["X-Forwarded-Proto"].ToString(),
                    context.Request.Headers["X-Forwarded-Uri"].ToString(),
                    context.Request.Headers["X-Forwarded-For"].ToString(),
                    context.Request.Headers.Host.ToString(),
                    context.Connection.RemoteIpAddress,
                    context.User),
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
            }
        }).DisableAntiforgery();
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
            ISecuritySettingsService securitySettingsService) =>
        {
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
            ISecuritySettingsService securitySettingsService) =>
        {
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
            PasskeyAuthenticationService passkeyAuthenticationService) =>
        {
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

            var otpCode = form["otpCode"].ToString();
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
                (IsEdgeGatewayReturnUrl(returnUrl) ||
                await passkeyAuthenticationService.ShouldOfferPasskeySetupAsync(
                    result.UserId.Value,
                    context.RequestAborted)))
            {
                context.Response.Redirect(BuildPasskeySetupRedirectTarget(returnUrl));
                return;
            }

            context.Response.Redirect(returnUrl);
        }).DisableAntiforgery().RequireRateLimiting("lms-auth-verify");
        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        }).DisableAntiforgery();
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
                    cancellationToken: context.RequestAborted) ?? new PasskeyEnrollmentOptionsRequest(null);
                var result = await passkeyAuthenticationService.BuildAuthenticatedRegistrationOptionsAsync(
                    context.User,
                    request.FriendlyName ?? string.Empty,
                    context.Request,
                    context.RequestAborted);

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

                var result = await passkeyAuthenticationService.CompleteRegistrationAsync(
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
                var request = await context.Request.ReadFromJsonAsync<PasskeyLoginOptionsRequest>(
                    cancellationToken: context.RequestAborted) ?? new PasskeyLoginOptionsRequest(null);
                var result = await passkeyAuthenticationService.BuildLoginOptionsAsync(
                    request.Email ?? string.Empty,
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

        if (path.StartsWithSegments("/access-denied") ||
            path.StartsWithSegments("/healthz") ||
            path.StartsWithSegments("/edge-auth/check") ||
            path.StartsWithSegments("/api/passkeys/login") ||
            path.StartsWithSegments("/internal/lms-tunnel") ||
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

    private static bool IsAuthenticationEntryPath(PathString path) =>
        IsInitialSetupPath(path) ||
        path.StartsWithSegments("/login") ||
        path.StartsWithSegments("/auth");

    private static string BuildPasskeySetupRedirectTarget(string returnUrl) =>
        $"/auth/setup-passkey?returnUrl={Uri.EscapeDataString(NormalizeReturnUrl(returnUrl))}";

    private static bool IsEdgeGatewayReturnUrl(string? returnUrl) =>
        NormalizeReturnUrl(returnUrl).StartsWith("/edge-auth/return", StringComparison.OrdinalIgnoreCase);

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

        return $"/InitialSetup?{string.Join("&", queryParts)}";
    }

    private static async Task<bool> ShouldRedirectToInitialSetupAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path;
        if (IsInitialSetupPath(path) ||
            IsAlwaysAnonymousAllowedPath(path) ||
            path.StartsWithSegments("/auth") ||
            path.StartsWithSegments("/api") ||
            path.StartsWithSegments("/internal"))
        {
            return false;
        }

        var securitySettingsService = context.RequestServices.GetRequiredService<ISecuritySettingsService>();
        var setup = await securitySettingsService.GetInitialSetupAsync(context.RequestAborted);
        return !setup.IsComplete;
    }

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

    private sealed record PasskeyEnrollmentOptionsRequest(string? FriendlyName);

    private sealed record PasskeyLoginOptionsRequest(string? Email);

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
