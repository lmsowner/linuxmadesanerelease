// Copyright (c) Openplan Software.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
