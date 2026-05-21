// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;
using LinuxMadeSane.DesktopHelper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<DesktopSessionHelperOptions>(builder.Configuration.GetSection("DesktopSession"));
builder.Services.AddSingleton<DesktopSessionEnvironmentDetector>();
builder.Services.AddSingleton<DesktopActionExecutor>();
builder.Services.AddSingleton<DesktopAssistantLaunchTicketCache>();
builder.Services.AddHostedService<DesktopSessionClientHostedService>();
builder.Services.AddHostedService<DesktopTrayHostedService>();

await builder.Build().RunAsync();
