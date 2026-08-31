using System.Collections.Concurrent;

using Docker.DotNet;
using Docker.DotNet.Models;

namespace ThetaNexus
{
    internal static class ContainerActions
    {
        private static readonly ConcurrentDictionary<string, string> _pending = new();

        internal static string? Pending(string id) => _pending.TryGetValue(id, out var verb)
            ? verb
            : null;

        internal static async Task StartStop(DockerClient client, ContainerListResponse container, CancellationToken token)
        {
            if (_pending.ContainsKey(container.ID))
                return;

            if (container.State is "running" or "restarting" or "paused")
            {
                _pending[container.ID] = "stopping";

                _ = client.Containers
                    .StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, token)
                    .ContinueWith(stop => _pending.TryRemove(container.ID, out _), TaskScheduler.Default);

                return;
            }

            _pending[container.ID] = "starting";

            try
            {
                await client.Containers
                    .StartContainerAsync(container.ID, new ContainerStartParameters(), token);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        }
    }
}