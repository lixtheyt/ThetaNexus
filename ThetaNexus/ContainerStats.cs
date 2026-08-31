using System.Collections.Concurrent;

using Docker.DotNet;
using Docker.DotNet.Models;

namespace ThetaNexus
{
    internal static class ContainerStats // this is for main list!!! not for details!!!
    {
        private static readonly ConcurrentDictionary<string, (double? Cpu, long Memory, long Limit)> _stats = new();

        private static string[] _tracked = [];

        internal static (double? Cpu, long Memory, long Limit)? Stats(string id) => _stats.TryGetValue(id, out var stats)
            ? stats
            : null;

        internal static void Track(IEnumerable<string> ids)
        {
            _tracked = [.. ids];

            foreach (var gone in _stats.Keys.Except(_tracked))
                _stats.TryRemove(gone, out _);
        }

        internal static async Task Watch(DockerClient client, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var id in _tracked)
                {
                    try
                    {
                        await client.Containers.GetContainerStatsAsync(id,
                            new ContainerStatsParameters { Stream = false },
                            new Progress<ContainerStatsResponse>(x =>
                            {
                                var cpuDelta = (double)x.CPUStats.CPUUsage.TotalUsage - x.PreCPUStats.CPUUsage.TotalUsage;
                                var systemDelta = (double)x.CPUStats.SystemUsage - x.PreCPUStats.SystemUsage;
                                var cpus = x.CPUStats.OnlineCPUs > 0 ? x.CPUStats.OnlineCPUs : 1;

                                var cache = x.MemoryStats.Stats is not null && x.MemoryStats.Stats.TryGetValue("inactive_file", out var inactive) ? inactive : 0;

                                _stats[id] = (systemDelta > 0 && cpuDelta > 0 ? cpuDelta / systemDelta * cpus * 100.0 : null,
                                    (long)(x.MemoryStats.Usage - cache),
                                    (long)x.MemoryStats.Limit);
                            }), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        _stats.TryRemove(id, out _);
                    }
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
