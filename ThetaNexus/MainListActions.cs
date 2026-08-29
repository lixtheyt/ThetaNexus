using System.Collections.Concurrent;

using Docker.DotNet;
using Docker.DotNet.Models;

namespace ThetaNexus
{
    internal static class MainListActions
    {
        internal static async Task StartStop(DockerClient client, ContainerListResponse container, ConcurrentDictionary<string, string> pending, CancellationToken token)
        {
            if (pending.ContainsKey(container.ID))
                return;

            if (container.State is "running" or "restarting" or "paused")
            {
                pending[container.ID] = "stopping";

                _ = client.Containers
                    .StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, token)
                    .ContinueWith(stop => pending.TryRemove(container.ID, out _), TaskScheduler.Default);

                return;
            }

            pending[container.ID] = "starting";

            try
            {
                await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), token);
            }
            finally
            {
                pending.TryRemove(container.ID, out _);
            }
        }

        internal static void DetailsContainer(DockerClient client, ContainerListResponse container)
        {

        }
    }
}
