// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Application;

public interface ILinuxMadeSanePluginModule
{
    void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath);
}
