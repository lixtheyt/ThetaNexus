using System.Collections.Concurrent;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ThetaNexus
{
    internal static class MainListContainerStats
    {
        private static readonly ConcurrentDictionary<string, (double? Cpu, long Memory, long Limit)> _stats = new();

        private static readonly ConcurrentDictionary<string, (ulong Cpu, ulong System)> _previous = new();

        private static string[] _tracked = [];

        internal static (double? Cpu, long Memory, long Limit)? Stats(string id) => _stats.GetValueOrDefault(id);

        internal static void Track(IEnumerable<string> ids)
        {
            _tracked = [..ids];

            foreach (var gone in _stats.Keys.Except(_tracked))
            {
                _stats.TryRemove(gone, out _);
                _previous.TryRemove(gone, out _);
            }
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
                            new ContainerStatsParameters { Stream = false, OneShot = true },
                            new Progress<ContainerStatsResponse>(x =>
                            {
                                var cpu = x.CPUStats.CPUUsage.TotalUsage;
                                var system = x.CPUStats.SystemUsage;
                                var cpus = x.CPUStats.OnlineCPUs > 0 ? x.CPUStats.OnlineCPUs : 1;

                                var cache = x.MemoryStats.Stats != null && x.MemoryStats.Stats.TryGetValue("inactive_file", out var inactive) ? inactive : 0;

                                _stats[id] = (_previous.TryGetValue(id, out var previous) && system > previous.System && cpu >= previous.Cpu
                                        ? (cpu - previous.Cpu) / (double)(system - previous.System) * cpus * 100.0
                                        : null,
                                    (long)Math.Min(x.MemoryStats.Usage, x.MemoryStats.Usage - cache),
                                    (long)x.MemoryStats.Limit);

                                _previous[id] = (cpu, system);
                            }), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // ignored
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