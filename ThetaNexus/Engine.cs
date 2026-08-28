using Docker.DotNet;

namespace ThetaNexus
{
    internal static class Engine
    {
        internal const string Pipe = "npipe://./pipe/dockerDesktopLinuxEngine";
        private const string PipeLegacy = "npipe://./pipe/docker_engine";

        internal static DockerClient Connect() =>
            new DockerClientConfiguration(new Uri(Pipe), defaultTimeout: TimeSpan.FromSeconds(2)).CreateClient();
    }
}
