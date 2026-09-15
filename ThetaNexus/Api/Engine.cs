using Docker.DotNet;

namespace ThetaNexus.Api
{
    internal static class Engine
    {
        internal const string Pipe = "npipe://./pipe/dockerDesktopLinuxEngine";

        internal static DockerClient Connect() =>
            new DockerClientConfiguration(new Uri(Pipe), defaultTimeout: TimeSpan.FromSeconds(2)).CreateClient();
    }
}
