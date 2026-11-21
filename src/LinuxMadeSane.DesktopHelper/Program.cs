// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.DesktopSession;
using LinuxMadeSane.DesktopHelper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<DesktopSessionHelperOptions>(builder.Configuration.GetSection("DesktopSession"));
builder.Services.AddSingleton<DesktopSessionEnvironmentDetector>();
builder.Services.AddSingleton<DesktopActionExecutor>();
builder.Services.AddSingleton<DesktopAssistantLaunchTicketCache>();
builder.Services.AddSingleton<DesktopAssistantNativeMessageBus>();
builder.Services.AddHostedService<DesktopSessionClientHostedService>();
builder.Services.AddHostedService<DesktopTrayHostedService>();

await builder.Build().RunAsync();
