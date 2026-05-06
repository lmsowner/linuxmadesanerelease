using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DisabledLmsConnectClientFeature : ILmsConnectClientFeature
{
    public bool IsAvailable => false;
    public bool SupportsRemoteAiSharing => false;
    public Type? IntegrationPageComponentType => null;
}
