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

        internal static (ConsoleKey Key, string Shown, string Label)[] Applicable(ContainerListResponse container, string? state = null)
        {
            var current = state ?? container.State;

            var live = current is "running" or "restarting";
            var paused = current == "paused";

            List<(ConsoleKey, string, string)> actions = 
            [
                (ConsoleKey.Spacebar, "␣", live || paused ? "stop" : "start"),
                (ConsoleKey.R, "r", "restart")
            ];

            if (live)
                actions.Add((ConsoleKey.P, "p", "paused"));

            if (paused)
                actions.Add((ConsoleKey.P, "p", "unpause"));

            if (live || paused)
                actions.Add((ConsoleKey.K, "k", "kill"));

            actions.Add((ConsoleKey.Delete, "Del", "remove"));
            actions.Add((ConsoleKey.L, "l", "logs"));

            if (live)
            {
                actions.Add((ConsoleKey.S, "s", "stats"));
                actions.Add((ConsoleKey.E, "e", "shell"));
            }

            if ((container.Ports ?? []).FirstOrDefault(x => x.PublicPort > 0) is { } published)
                actions.Add((ConsoleKey.O, "o", $"open {published.PublicPort} is browser"));

            return [..actions];
        }

        internal static async Task StartStop(DockerClient client, ContainerListResponse container, string? state, CancellationToken token)
        {
            var name = container.Names[0].TrimStart('/');
            var current = state ?? container.State;

            if (_pending.ContainsKey(container.ID))
                return;

            if (current is "running" or "restarting" or "paused")
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
                            ? ($"{name} could not stop: {UI.Reason(failure)}", Models.Outcome.Failed)
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

                _notice = ($"{name} started", Models.Outcome.Succeeded);
            }
            catch (DockerApiException failure)
            {
                _notice = ($"{name} could not start: {UI.Reason(failure)}", Models.Outcome.Failed);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        }

        internal static async Task Restart(DockerClient client, ContainerListResponse container, CancellationToken token)
        {
            var name = container.Names[0].TrimStart('/');

            if (!_pending.TryAdd(container.ID, "restarting"))
                return;

            var started = DateTime.UtcNow;

            try
            {
                await client.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters { WaitBeforeKillSeconds = KillAfterSeconds }, token);

                var took = (DateTime.UtcNow - started).TotalSeconds;

                _notice = took >= KillAfterSeconds
                    ? ($"{name} restarted after {took.ToString("0.0", CultureInfo.InvariantCulture)}s, it ignored SIGTERM", Models.Outcome.Warned)
                    : ($"{name} restarted in {took.ToString("0.0", CultureInfo.InvariantCulture)}s", Models.Outcome.Succeeded);
            }
            catch (DockerApiException failure)
            {
                _notice = ($"{name} could not restart: {UI.Reason(failure)}", Models.Outcome.Failed);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        } 

        internal static async Task Pause(DockerClient client, ContainerListResponse container, string? state, CancellationToken token)
        {
            var name = container.Names[0].TrimStart('/');
            var resume = (state ?? container.State) == "paused";

            if (!_pending.TryAdd(container.ID, resume ? "resuming" : "pausing"))
                return;

            try
            {
                if (resume)
                    await client.Containers.UnpauseContainerAsync(container.ID, token);
                else
                    await client.Containers.PauseContainerAsync(container.ID, token);

                _notice = ($"{name} {(resume ? "resumed" : "paused")}", Models.Outcome.Succeeded);
            }
            catch (DockerApiException failure)
            {
                _notice = ($"{name} could not {(resume ? "resume" : "pause")}: {UI.Reason(failure)}", Models.Outcome.Failed);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        }

        internal static async Task Kill(DockerClient client, ContainerListResponse container, CancellationToken token)
        {
            var name = container.Names[0].TrimStart('/');

            if (!_pending.TryAdd(container.ID, "killing"))
                return;

            try
            {
                await client.Containers.KillContainerAsync(container.ID, new ContainerKillParameters(), token);

                _notice = ($"{name} killed with SIGKILL", Models.Outcome.Warned);
            }
            catch (DockerApiException failure)
            {
                _notice = ($"{name} could not be killed: {UI.Reason(failure)}", Models.Outcome.Failed);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        }

        internal static async Task Remove(DockerClient client, ContainerListResponse container, CancellationToken token)
        {
            var name = container.Names[0].TrimStart('/');

            if (!_pending.TryAdd(container.ID, "removing"))
                return;

            try
            {
                await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters(), token);

                _notice = ($"{name} removed", Models.Outcome.Succeeded);
            }
            catch (DockerApiException failure)
            {
                _notice = ($"{name} could not be removed: {UI.Reason(failure)}", Models.Outcome.Failed);
            }
            finally
            {
                _pending.TryRemove(container.ID, out _);
            }
        }
    }
}