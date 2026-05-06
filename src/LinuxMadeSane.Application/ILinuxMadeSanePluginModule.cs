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
