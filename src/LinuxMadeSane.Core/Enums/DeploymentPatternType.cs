namespace LinuxMadeSane.Core.Enums;

public enum DeploymentPatternType
{
    AspNetKestrel = 0,
    NodeService = 1,
    PythonApp = 2,
    DockerBackedService = 3,
    WorkerService = 4
}
