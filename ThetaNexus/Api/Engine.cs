using Docker.DotNet;

namespace ThetaNexus.Api
{
    internal static class Engine
    {
        internal static readonly string Endpoint = OperatingSystem.IsWindows()
            ? "npipe://./pipe/dockerDesktopLinuxEngine"
            : Environment.GetEnvironmentVariable("DOCKER_HOST") is { } host && (host.StartsWith("unix://") || host.StartsWith("tcp://"))
                ? host
                : "unix://" + (new[]
                {
                    "/var/run/docker.sock",
                    $"{Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")}/docker.sock",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "run", "docker.sock"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "desktop", "docker.sock"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima", "default", "docker.sock"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orbstack", "run", "docker.sock"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rd", "docker.sock")
                }.FirstOrDefault(File.Exists) ?? "/var/run/docker.sock");

        internal static DockerClient Connect() =>
            new DockerClientConfiguration(new Uri(Endpoint), defaultTimeout: TimeSpan.FromSeconds(2)).CreateClient();
    }
}
