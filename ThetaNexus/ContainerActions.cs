using System.Collections.Concurrent;
using ThetaNexus.Shared;
using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ThetaNexus
{
    internal static class ContainerActions
    {
        private const int KillAfterSeconds = 10;

        private static readonly ConcurrentDictionary<string, string> _pending = new();

        private static (string Text, Models.Outcome Outcome)? _notice;

        internal static (string Text, Models.Outcome Outcome)? Notice()
        {
            var notice = _notice;

            _notice = null;

            return notice;
        }

        internal static string? Pending(string id) => _pending.GetValueOrDefault(id);

        internal static async Task StartStop(DockerClient client, ContainerListResponse container, CancellationToken token)
        {
            var name = container.Names[0].TrimStart('/');

            if (_pending.ContainsKey(container.ID))
                return;

            if (container.State is "running" or "restarting" or "paused")
            {
                _pending[container.ID] = "stopping";

                var started = DateTime.UtcNow;

                _ = client.Containers
                    .StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = KillAfterSeconds }, token)
                    .ContinueWith(stop =>
                    {
                        _pending.TryRemove(container.ID, out _);

                        if (stop.IsCanceled)
                            return;

                        var took = (DateTime.UtcNow - started).TotalSeconds;

                        _notice = stop.Exception?.GetBaseException() is { } failure
                            ? ($"{name} could not stop: {failure.Message}", Models.Outcome.Failed)
                            : took >= KillAfterSeconds
                                ? ($"{name} killed after {took.ToString("0.0", CultureInfo.InvariantCulture)}s, it ignored SIGTERM", Models.Outcome.Warned)
                                : ($"{name} stopped in {took.ToString("0.0", CultureInfo.InvariantCulture)}s", Models.Outcome.Succeeded);
                    }, TaskScheduler.Default);

                return;
            }

            _pending[container.ID] = "starting";

            try
            {
                await client.Containers
                    .StartContainerAsync(container.ID, new ContainerStartParameters(), token);
            }
            catch (DockerApiException failure)
            {
                _notice = ($"{name} could not start: {failure.Message}", Models.Outcome.Failed);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        }
    }
}