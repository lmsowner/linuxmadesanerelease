namespace LinuxMadeSane.Core.Abstractions;

public interface ILmsConnectClientFeature
{
    bool IsAvailable { get; }
    bool SupportsRemoteAiSharing { get; }
    Type? IntegrationPageComponentType { get; }
}
