using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using Color = Spectre.Console.Color;
using ThetaNexus.Shared;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ThetaNexus
{
    internal static class MainList
    {
        internal static async Task Display(DockerClient client)
        {
            var sections = Enum.GetNames<Models.MainListSections>()
                .Select(x => x.ToLower())
                .ToArray();
            var sorts = new[]
            {
                Enum.GetNames<Models.MainListSorts>()
                    .Select(x => x.ToUpper())
                    .ToArray(),
                Enum.GetNames<Models.MainListImageSorts>()
                    .Select(x => x.ToUpper())
                    .ToArray(),
                Enum.GetNames<Models.MainListVolumeSorts>()
                    .Select(x => x.ToUpper())
                    .ToArray(),
                Enum.GetNames<Models.MainListNetworkSorts>()
                    .Select(x => x.ToUpper())
                    .ToArray(),
                []
            };
            var kinds = new[] { "all", "container", "image", "volume", "network" };

            var collapsed = new HashSet<string>();
            var selected = 0;
            var section = 0;
            var sortBy = new int[sections.Length];
            var descending = new bool[sections.Length];

            descending[(int)Models.MainListSections.Events] = true;

            var filter = string.Empty;
            List<Message> log = [];
            var frozen = 0;
            var paused = false;
            var kind = 0;
            var menu = false;
            var chosen = 0;
            var filtering = false;
            ConsoleKey? confirm = null;
            var question = string.Empty;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;

            while (true)
            {
                using var cts = new CancellationTokenSource();

                var events = Task.CompletedTask;
                var stats = Task.CompletedTask;

                string[]? shell = null;

                try
                {
                    await client.System.PingAsync(cts.Token);

                    var version = await client.System.GetVersionAsync(cts.Token);

                    var engine = $"engine {version.Version}";

                    var hostMemory = (await client.System.GetSystemInfoAsync(cts.Token)).MemTotal;

                    var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cts.Token);

                    IList<ImagesListResponse> images = [];
                    IList<VolumeResponse> volumes = [];
                    IList<NetworkResponse> networks = [];

                    stats = Task.Run(()
                        => MainListContainerStats.Watch(client, cts.Token), cts.Token);

                    var refreshed = DateTime.UtcNow;
                    var stale = true;
                    var dirty = true;

                    events = Task.Run(async () =>
                    {
                        try
                        {
                            await client.System.MonitorEventsAsync(new ContainerEventsParameters(), new Progress<Message>(message =>
                            {
                                stale = true;

                                lock (log)
                                {
                                    log.Add(message);

                                    if (log.Count > 500)
                                        log.RemoveAt(0);
                                }
                            }), cts.Token);
                        }
                        catch (Exception)
                        {
                            stale = true;
                        }
                    }, cts.Token);

                    AnsiConsole.Clear();

                    await AnsiConsole.Live(new Markup(string.Empty))
                        .StartAsync(async ctx =>
                        {
                            while (true)
                            {
                                if (!menu && (stale || DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(2)))
                                {
                                    stale = false;
                                    refreshed = DateTime.UtcNow;
                                    containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cts.Token);
                                    dirty = true;

                                    switch ((Models.MainListSections)section)
                                    {
                                        case Models.MainListSections.Images:
                                            images = await client.Images.ListImagesAsync(new ImagesListParameters(), cts.Token);
                                            break;
                                        case Models.MainListSections.Volumes:
                                            volumes = (await client.Volumes.ListAsync(cts.Token)).Volumes;
                                            break;
                                        case Models.MainListSections.Networks:
                                            networks = await client.Networks.ListNetworksAsync(new NetworksListParameters(), cts.Token);
                                            break;
                                    }
                                }

                                IList<ContainerListResponse> matching = filter.Length == 0
                                    ? containers
                                    : [..containers.Where(x => x.Names[0].Contains(filter, StringComparison.OrdinalIgnoreCase)
                                        || x.Image.Contains(filter, StringComparison.OrdinalIgnoreCase))];

                                var groups = matching
                                    .GroupBy(x => x.Labels != null && x.Labels.TryGetValue("com.docker.compose.project", out var project) ? project : "no project")
                                    .OrderBy(x => x.Key == "no project")
                                    .ThenBy(x => x.Key)
                                    .ToList();

                                var rows = new List<(string Project, ContainerListResponse? Container)>();

                                foreach (var group in groups)
                                {
                                    rows.Add((group.Key, null));

                                    if (collapsed.Contains(group.Key))
                                        continue;

                                    var ordered = sortBy[(int)Models.MainListSections.Containers] switch
                                    {
                                        1 => group.OrderBy(x => x.State),
                                        2 => group.OrderBy(x => x.Image),
                                        3 => group.OrderBy(x => (x.Ports ?? []).Where(p => p.PublicPort > 0).Select(p => (int)p.PublicPort).DefaultIfEmpty(0).Max()),
                                        6 => group.OrderBy(x => x.Created),
                                        _ => group.OrderBy(x => x.Names[0])
                                    };

                                    rows.AddRange((descending[(int)Models.MainListSections.Containers] ? ordered.Reverse() : ordered)
                                        .Select(x => (group.Key, (ContainerListResponse?)x)));
                                }

                                var imagesOrdered = sortBy[(int)Models.MainListSections.Images] switch
                                {
                                    1 => images.OrderBy(x => (x.RepoTags ?? []).FirstOrDefault() ?? string.Empty, StringComparer.Ordinal),
                                    2 => images.OrderBy(x => x.Size).ThenBy(x => x.ID, StringComparer.Ordinal),
                                    3 => images.OrderBy(x => x.Created).ThenBy(x => x.ID, StringComparer.Ordinal),
                                    4 => images.OrderBy(x => x.Containers).ThenBy(x => x.ID, StringComparer.Ordinal),
                                    _ => images.OrderBy(x => (x.RepoTags ?? []).FirstOrDefault() ?? string.Empty, StringComparer.Ordinal)
                                };

                                IList<ImagesListResponse> imagesVisible = [..(descending[(int)Models.MainListSections.Images] ? imagesOrdered.Reverse() : imagesOrdered)
                                    .Where(x => filter.Length == 0 || (x.RepoTags ?? []).Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))];

                                var volumesOrdered = sortBy[(int)Models.MainListSections.Volumes] switch
                                {
                                    1 => volumes.OrderBy(x => x.Driver, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal),
                                    2 => volumes.OrderBy(x => containers.Count(c => (c.Mounts ?? []).Any(m => m.Name == x.Name))).ThenBy(x => x.Name, StringComparer.Ordinal),
                                    3 => volumes.OrderBy(x => x.CreatedAt, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal),
                                    _ => volumes.OrderBy(x => x.Name, StringComparer.Ordinal)
                                };

                                IList<VolumeResponse> volumesVisible = [..(descending[(int)Models.MainListSections.Volumes] ? volumesOrdered.Reverse() : volumesOrdered)
                                    .Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];

                                var networksOrdered = sortBy[(int)Models.MainListSections.Networks] switch
                                {
                                    1 => networks.OrderBy(x => x.Driver ?? string.Empty, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal),
                                    2 => networks.OrderBy(x => (x.IPAM?.Config ?? []).Select(c => c.Subnet).FirstOrDefault() ?? string.Empty, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal),
                                    3 => networks.OrderBy(x => containers.Count(c => c.State is "running" or "paused" && (c.NetworkSettings?.Networks?.ContainsKey(x.Name) ?? false))).ThenBy(x => x.Name, StringComparer.Ordinal),
                                    _ => networks.OrderBy(x => x.Name, StringComparer.Ordinal)
                                };

                                IList<NetworkResponse> networksVisible = [..(descending[(int)Models.MainListSections.Networks] ? networksOrdered.Reverse() : networksOrdered)
                                    .Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];

                                var count = (Models.MainListSections)section switch
                                {
                                    Models.MainListSections.Containers => rows.Count,
                                    Models.MainListSections.Images => imagesVisible.Count,
                                    Models.MainListSections.Volumes => volumesVisible.Count,
                                    Models.MainListSections.Networks => networksVisible.Count,
                                    _ => 0
                                };

                                selected = Math.Clamp(selected, 0, Math.Max(0, count - 1));

                                var actions = section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } acting
                                    ? ContainerActions.Applicable(acting)
                                    : [];

                                chosen = Math.Clamp(chosen, 0, Math.Max(0, actions.Length - 1));

                                if (Console.KeyAvailable)
                                {
                                    var key = Console.ReadKey(intercept: true);
                                    var pressed = key.Key;

                                    notice = null;
                                    noticed = DateTime.UtcNow;

                                    if (confirm is { } pending)
                                    {
                                        if (key.Key == ConsoleKey.Y)
                                            switch (pending)
                                            {
                                                case ConsoleKey.X when rows.Count > 0 && rows[selected].Container is { } removing:
                                                    await ContainerActions.Remove(client, removing, cts.Token);
                                                    stale = true;
                                                    break;
                                                case ConsoleKey.Delete when section == (int)Models.MainListSections.Containers:
                                                    {
                                                        var pruned = await client.Containers.PruneContainersAsync(new ContainersPruneParameters(), cts.Token);
                                                        var removed = pruned.ContainersDeleted?.Count ?? 0;

                                                        notice = removed > 0
                                                            ? ($"removed {removed} stopped containers, reclaimed {UI.Size((long)pruned.SpaceReclaimed)}", Models.Outcome.Succeeded)
                                                            : ("nothing to prune, every container is still running", Models.Outcome.Warned);
                                                        noticed = DateTime.UtcNow;
                                                        stale = true;
                                                        break;
                                                    }
                                                case ConsoleKey.Delete when section == (int)Models.MainListSections.Images:
                                                    {
                                                        var pruned = await client.Images.PruneImagesAsync(new ImagesPruneParameters(), cts.Token);
                                                        var removed = pruned.ImagesDeleted?.Count ?? 0;

                                                        notice = removed > 0
                                                            ? ($"deleted {removed} images, reclaimed {UI.Size((long)pruned.SpaceReclaimed)}", Models.Outcome.Succeeded)
                                                            : ("nothing to prune, no untagged leftovers, tagged images are kept", Models.Outcome.Warned);
                                                        noticed = DateTime.UtcNow;
                                                        stale = true;
                                                        break;
                                                    }
                                                case ConsoleKey.Delete when section == (int)Models.MainListSections.Volumes:
                                                    {
                                                        var pruned = await client.Volumes.PruneAsync(new VolumesPruneParameters(), cts.Token);
                                                        var removed = pruned.VolumesDeleted?.Count ?? 0;

                                                        notice = removed > 0
                                                            ? ($"deleted {removed} volumes, reclaimed {UI.Size((long)pruned.SpaceReclaimed)}", Models.Outcome.Succeeded)
                                                            : ("nothing to prune, no unnamed leftovers, named volumes are kept", Models.Outcome.Warned);
                                                        noticed = DateTime.UtcNow;
                                                        stale = true;
                                                        break;
                                                    }
                                                case ConsoleKey.Delete when section == (int)Models.MainListSections.Networks:
                                                    {
                                                        var pruned = await client.Networks.PruneNetworksAsync(new NetworksDeleteUnusedParameters(), cts.Token);
                                                        var removed = pruned.NetworksDeleted?.Count ?? 0;
                                                        var custom = networks.Count(x => x.Name is not ("bridge" or "host" or "none"));

                                                        notice = removed > 0
                                                            ? ($"deleted {removed} networks", Models.Outcome.Succeeded)
                                                            : custom > 0
                                                                ? ($"nothing to prune, {custom} custom networks are in use", Models.Outcome.Warned)
                                                                : ("nothing to prune, only bridge, host and none exist", Models.Outcome.Warned);
                                                        noticed = DateTime.UtcNow;
                                                        stale = true;
                                                        break;
                                                    }
                                            }

                                        confirm = null;
                                        dirty = true;
                                        continue;
                                    }

                                    if (filtering)
                                    {
                                        filter = key.Key switch
                                        {
                                            ConsoleKey.Escape => string.Empty,
                                            ConsoleKey.Backspace => filter.Length > 0 ? filter[..^1] : filter,
                                            _ => char.IsControl(key.KeyChar) ? filter : filter + key.KeyChar
                                        };

                                        filtering = key.Key is not (ConsoleKey.Escape or ConsoleKey.Enter);
                                        selected = 0;
                                        dirty = true;
                                        continue;
                                    }

                                    if (menu)
                                    {
                                        menu = key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow;

                                        chosen = key.Key switch
                                        {
                                            ConsoleKey.UpArrow => Math.Max(0, chosen - 1),
                                            ConsoleKey.DownArrow => Math.Min(actions.Length - 1, chosen + 1),
                                            _ => chosen
                                        };

                                        dirty = true;

                                        if (menu || key.Key != ConsoleKey.Enter || actions.Length == 0)
                                            continue;

                                        pressed = actions[Math.Min(chosen, actions.Length - 1)].Key;
                                    }

                                    switch (pressed)
                                    {
                                        case var _ when key.KeyChar == '/':
                                            filtering = true;
                                            break;
                                        case ConsoleKey.UpArrow:
                                            selected = Math.Max(0, selected - 1);
                                            break;
                                        case ConsoleKey.DownArrow:
                                            selected = Math.Min(count - 1, selected + 1);
                                            break;
                                        case ConsoleKey.LeftArrow:
                                            section = (section + sections.Length - 1) % sections.Length;
                                            selected = 0;
                                            stale = true;
                                            break;
                                        case ConsoleKey.RightArrow:
                                            section = (section + 1) % sections.Length;
                                            selected = 0;
                                            stale = true;
                                            break;
                                        case >= ConsoleKey.D1 and <= ConsoleKey.D5:
                                            section = key.Key - ConsoleKey.D1;
                                            selected = 0;
                                            stale = true;
                                            break;
                                        case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                            descending[section] = !descending[section];
                                            break;
                                        case ConsoleKey.Tab:
                                            sortBy[section] = (sortBy[section] + 1) % Math.Max(1, sorts[section].Length);
                                            break;
                                        case ConsoleKey.C when section == (int)Models.MainListSections.Containers && rows.Count > 0:
                                            var toToggle = rows[selected].Project;

                                            if (collapsed.Remove(toToggle))
                                                break;

                                            collapsed.Add(toToggle);
                                            selected = rows.FindIndex(x => x.Project == toToggle && x.Container == null);
                                            break;
                                        case ConsoleKey.Spacebar when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } target:
                                            await ContainerActions.StartStop(client, target, null, cts.Token);
                                            break;
                                        case ConsoleKey.Spacebar when section == (int)Models.MainListSections.Events:
                                            paused = !paused;

                                            if (paused)
                                            {
                                                lock (log)
                                                    frozen = log.Count;
                                            }

                                            break;
                                        case ConsoleKey.C when section == (int)Models.MainListSections.Events:
                                            lock (log)
                                                log.Clear();

                                            frozen = 0;
                                            paused = false;
                                            break;
                                        case ConsoleKey.F when section == (int)Models.MainListSections.Events:
                                            kind = (kind + 1) % kinds.Length;
                                            break;
                                        case ConsoleKey.R when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } restarting:
                                            await ContainerActions.Restart(client, restarting, cts.Token);
                                            break;
                                        case ConsoleKey.P when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } pausing:
                                            await ContainerActions.Pause(client, pausing, null, cts.Token);
                                            break;
                                        case ConsoleKey.K when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } killing:
                                            await ContainerActions.Kill(client, killing, cts.Token);
                                            break;
                                        case ConsoleKey.X when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } removing:
                                            confirm = ConsoleKey.X;
                                            question = $"Remove {removing.Names[0].TrimStart('/')} permanently?";
                                            break;
                                        case ConsoleKey.Delete when section == (int)Models.MainListSections.Containers:
                                            {
                                                var stopped = containers.Count(x => x.State == "exited");

                                                if (stopped == 0)
                                                {
                                                    notice = ("nothing to prune, every container is still running", Models.Outcome.Warned);
                                                    noticed = DateTime.UtcNow;
                                                    break;
                                                }

                                                confirm = ConsoleKey.Delete;
                                                question = $"Remove all {stopped} stopped containers permanently?";
                                                break;
                                            }
                                        case var _ when key.KeyChar == '.' && actions.Length > 0:
                                            menu = true;
                                            chosen = 0;
                                            break;
                                        case var _ when key.KeyChar == '?':
                                            await Help.Display(ctx, cts.Token);
                                            break;
                                        case ConsoleKey.Delete when section == (int)Models.MainListSections.Images:
                                            confirm = ConsoleKey.Delete;
                                            question = "Prune untagged images?";
                                            break;
                                        case ConsoleKey.Delete when section == (int)Models.MainListSections.Volumes:
                                            confirm = ConsoleKey.Delete;
                                            question = "Prune unnamed volumes?";
                                            break;
                                        case ConsoleKey.Delete when section == (int)Models.MainListSections.Networks:
                                            confirm = ConsoleKey.Delete;
                                            question = "Prune unused networks?";
                                            break;
                                        case ConsoleKey.Q:
                                            return;
                                        case ConsoleKey.N when section == (int)Models.MainListSections.Images && imagesVisible.Count > 0:
                                            {
                                                var made = await RunContainer.Display(client, ctx, imagesVisible[selected], containers, cts.Token);

                                                if (made != null)
                                                {
                                                    notice = ($"{made} created and started, press 1 for its logs", Models.Outcome.Succeeded);
                                                    noticed = DateTime.UtcNow;
                                                    section = (int)Models.MainListSections.Containers;
                                                    stale = true;
                                                }

                                                break;
                                            }
                                        case ConsoleKey.Enter when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } open:
                                            shell = await ContainerDetails.Display(client, ctx, open, cts.Token);

                                            if (shell != null)
                                                return;

                                            break;
                                        case ConsoleKey.L when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } logging:
                                            await ContainerDetails.DisplayLogs(client, ctx, logging, cts.Token);
                                            break;
                                        case ConsoleKey.S when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } watching:
                                            await ContainerStats.Display(client, ctx, watching, cts.Token);
                                            break;
                                        case ConsoleKey.E when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { State: "running" } shelling:
                                            shell = ["exec", "-it", shelling.ID, "sh", "-c", "command -v bash >/dev/null && exec bash || exec sh"];
                                            return;
                                        case ConsoleKey.O when section == (int)Models.MainListSections.Containers && rows.Count > 0 && rows[selected].Container is { } opening
                                            && (opening.Ports ?? []).FirstOrDefault(x => x.PublicPort > 0) is { } published:
                                            try
                                            {
                                                Process.Start(new ProcessStartInfo($"http://localhost:{published.PublicPort}") { UseShellExecute = true })?.Dispose();
                                            }
                                            catch (Exception browser)
                                            {
                                                notice = ($"could not open localhost:{published.PublicPort}, {browser.Message}", Models.Outcome.Failed);
                                                noticed = DateTime.UtcNow;
                                            }

                                            break;
                                        case ConsoleKey.Enter when section == (int)Models.MainListSections.Images && imagesVisible.Count > 0:
                                            shell = await ImageDetails.Display(client, ctx, imagesVisible[selected], cts.Token);

                                            if (shell != null)
                                                return;

                                            break;
                                        case ConsoleKey.Enter when section == (int)Models.MainListSections.Volumes && volumesVisible.Count > 0:
                                            shell = await VolumeInfo.Display(client, ctx, volumesVisible[selected], cts.Token);

                                            if (shell != null)
                                                return;

                                            break;
                                        case ConsoleKey.Enter when section == (int)Models.MainListSections.Networks && networksVisible.Count > 0:
                                            await NetworkInfo.Display(client, ctx, networksVisible[selected], cts.Token);
                                            break;
                                    }

                                    dirty = true;
                                    continue;
                                }

                                if (ContainerActions.Notice() is { } reported)
                                {
                                    notice = reported;
                                    noticed = DateTime.UtcNow;
                                    dirty = true;
                                }

                                if (notice != null && DateTime.UtcNow - noticed > TimeSpan.FromSeconds(4))
                                {
                                    notice = null;
                                    dirty = true;
                                }

                                if (!dirty)
                                {
                                    await Task.Delay(50, cts.Token);
                                    continue;
                                }

                                dirty = false;

                                var width = AnsiConsole.Profile.Width;
                                var height = Console.WindowHeight;
                                var body = width - 4;
                                var bodyHeight = Math.Max(1, height - 9 - (notice == null ? 0 : 1));

                                var page = new List<IRenderable>
                                {
                                    new Grid { Expand = true }
                                        .AddColumn(new GridColumn())
                                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                                        .AddRow("[bold]ThetaNexus[/]", $"[{Color.Grey.ToMarkup()}]{engine} · {containers.Count(x => x.State == "running")}/{containers.Count} running[/]"
                                            + (count > 0 ? $" [{Color.Grey35}]·[/] [{Color.Khaki1}]{selected + 1}[/][{Color.Grey35}]/{count}[/]" : string.Empty)),
                                    new Rule { Style = new Style(Color.Grey35) },
                                    new Markup(UI.Spread(
                                    [
                                        .. sections.Select((x, i) => (i == section ? x.ToUpperInvariant() : x, i == section ? (Color?)Color.SteelBlue1 : Color.Grey35)),
                                        (filtering ? $"/{filter}_" : filter.Length > 0 ? $"/{filter}" : "/ filter",
                                            filtering ? (Color?)Color.SteelBlue1 : filter.Length > 0 ? Color.Khaki1 : Color.Grey35)
                                    ], body)),
                                    new Rule { Style = new Style(Color.Grey35) },
                                    new Text(string.Empty)
                                };

                                MainListContainerStats.Track(containers
                                    .Where(x => x.State == "running")
                                    .Select(x => x.ID));

                                var drawn = 0;

                                if (menu && rows[selected].Container is { } shown)
                                {
                                    var state = UI.Glyph(shown);

                                    page.Add(UI.Menu(
                                    [
                                        (shown.Names[0].TrimStart('/'), Color.SteelBlue1),
                                        (state.Text.Replace("  ", " "), state.Color)
                                    ],
                                        [.. actions.Select(x => (x.Shown, x.Label))],
                                        chosen,
                                        body));

                                    drawn = actions.Length + 8;
                                }
                                else
                                    switch ((Models.MainListSections)section)
                                    {
                                        case Models.MainListSections.Containers:
                                            {
                                                var showImage = width >= 70;
                                                var showPorts = width >= 90;
                                                var showCpu = width >= 90;
                                                var showMem = width >= 110;

                                                var nameWidth = Math.Clamp(containers.Count == 0 ? 14 : containers.Max(x => x.Names[0].TrimStart('/').Length) + 2, 14, 28);
                                                const int stateWidth = 16;
                                                var portsWidth = showPorts ? 15 : 0;
                                                var cpuWidth = showCpu ? 8 : 0;
                                                var memWidth = showMem ? 14 : 0;
                                                const int upWidth = 8;
                                                var room = showImage ? body - 2 - nameWidth - stateWidth - portsWidth - cpuWidth - memWidth - upWidth : 0;

                                                showImage = room >= 10;

                                                var imageWidth = showImage ? room : 0;

                                                var first = selected / bodyHeight * bodyHeight;
                                                var last = Math.Min(first + bodyHeight, rows.Count);

                                                var titles = new List<(string, Color?)> { ("  ", null) };

                                                for (int i = 0; i < sorts[section].Length; i++)
                                                {
                                                    var titleWidth = i switch
                                                    {
                                                        0 => nameWidth,
                                                        1 => stateWidth,
                                                        2 => imageWidth,
                                                        3 => portsWidth,
                                                        4 => cpuWidth,
                                                        5 => memWidth,
                                                        _ => upWidth
                                                    };

                                                    if (titleWidth == 0)
                                                        continue;

                                                    var (text, color) = Title(i, i is 4 or 5 ? titleWidth - 1 : titleWidth, i is 4 or 5 or 6);

                                                    titles.Add(i is 4 or 5 ? (text + " ", color) : (text, color));
                                                }

                                                page.Add(new Markup(UI.Compose([.. titles], body, false)));

                                                if (rows.Count == 0)
                                                {
                                                    page.Add(new Markup($"[{Color.Grey.ToMarkup()}]No containers on this engine.[/]"));

                                                    page.Add(new Text(string.Empty));
                                                    page.Add(new Markup($"[{Color.Grey35}]  docker run --rm hello-world[/]"));

                                                    drawn = 3;
                                                }

                                                for (int i = first; i < last; i++)
                                                {
                                                    var (project, container) = rows[i];

                                                    drawn++;

                                                    if (container == null)
                                                    {
                                                        page.Add(new Markup(UI.Compose(
                                                        [
                                                            (collapsed.Contains(project) ? "▶ " : "▼ ", Color.Grey),
                                                        (UI.Crop(project, Math.Max(8, body - 12)), Color.Khaki1),
                                                        ($" ({groups.First(x => x.Key == project).Count()})", Color.Grey35)
                                                        ], body, i == selected)));

                                                        continue;
                                                    }

                                                    var (glyph, color) = ContainerActions.Pending(container.ID) is { } verb
                                                        ? ($"◌  {verb}", Color.Yellow)
                                                        : UI.Glyph(container);

                                                    var ports = (container.Ports ?? [])
                                                        .Where(x => x.PublicPort > 0)
                                                        .Select(x => $"{x.PublicPort}→{x.PrivatePort}")
                                                        .Distinct()
                                                        .ToList();

                                                    var age = DateTime.UtcNow - container.Created;

                                                    var cells = new List<(string, Color?)>
                                                {
                                                    ("  ", null),
                                                    (UI.Crop(container.Names[0].TrimStart('/'), nameWidth - 1).PadRight(nameWidth), Color.SteelBlue1),
                                                    (glyph.PadRight(stateWidth), color)
                                                };

                                                    if (showImage)
                                                        cells.Add((UI.Crop(container.Image, imageWidth - 1).PadRight(imageWidth), Color.MediumPurple2));

                                                    if (showPorts)
                                                        cells.Add((UI.Crop(ports.Count switch
                                                        {
                                                            0 => "–",
                                                            1 => ports[0],
                                                            _ => $"{ports[0]} +{ports.Count - 1}"
                                                        }, portsWidth - 1).PadRight(portsWidth), Color.Aqua));

                                                    if (showCpu)
                                                        cells.Add((MainListContainerStats.Stats(container.ID)?.Cpu is { } cpu
                                                            ? (UI.Crop(cpu.ToString("0.0", CultureInfo.InvariantCulture), cpuWidth - 3) + "%").PadLeft(cpuWidth - 1) + " "
                                                            : "–".PadLeft(cpuWidth - 1) + " ", Color.Grey35));

                                                    if (showMem)
                                                        cells.Add((UI.Crop(MainListContainerStats.Stats(container.ID) is { } used
                                                            ? used.Limit > 0 && (hostMemory <= 0 || used.Limit < hostMemory)
                                                                ? $"{used.Memory / 1024 / 1024}/{used.Limit / 1024 / 1024} MB"
                                                                : $"{used.Memory / 1024 / 1024} MB"
                                                            : "–", memWidth - 2).PadLeft(memWidth - 1) + " ", Color.Grey35));

                                                    cells.Add(((age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s"
                                                        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
                                                        : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
                                                        : $"{(int)age.TotalDays}d").PadLeft(upWidth), Color.CadetBlue));

                                                    page.Add(new Markup(UI.Compose([.. cells], body, i == selected)));
                                                }

                                                break;
                                            }
                                        case Models.MainListSections.Images:
                                            {
                                                var showId = width >= 90;
                                                var showCreated = width >= 110;

                                                const int tagWidth = 16;
                                                var idWidth = showId ? 15 : 0;
                                                const int sizeWidth = 13;
                                                var createdWidth = showCreated ? 12 : 0;
                                                const int usedWidth = 9;
                                                var repoWidth = Math.Max(12, body - 2 - tagWidth - idWidth - sizeWidth - createdWidth - usedWidth);

                                                page.Add(new Markup(UI.Compose(
                                                [
                                                    ("  ", null),
                                                Title(0, repoWidth, false),
                                                Title(1, tagWidth, false),
                                                (showId ? "ID".PadRight(idWidth) : string.Empty, Color.Grey35),
                                                Title(2, sizeWidth - 3, true),
                                                ("   ", null),
                                                (showCreated ? Title(3, createdWidth, false) : (string.Empty, (Color?)null)),
                                                Title(4, usedWidth, true)
                                                ], body, false)));

                                                if (imagesVisible.Count == 0)
                                                {
                                                    page.Add(new Markup($"[{Color.Grey.ToMarkup()}]No images on this engine.[/]"));

                                                    drawn = 1;
                                                    break;
                                                }

                                                var from = selected / bodyHeight * bodyHeight;
                                                var to = Math.Min(from + bodyHeight, imagesVisible.Count);

                                                for (int i = from; i < to; i++)
                                                {
                                                    var image = imagesVisible[i];

                                                    var tagged = (image.RepoTags ?? []).FirstOrDefault() ?? "<none>:<none>";
                                                    var colon = tagged.LastIndexOf(':');
                                                    var hasTag = colon > tagged.LastIndexOf('/');

                                                    var repository = hasTag ? tagged[..colon] : tagged;
                                                    var tag = hasTag ? tagged[(colon + 1)..] : "<none>";
                                                    var dangling = repository == "<none>";

                                                    var more = image.RepoTags is { Count: > 1 } ? $" +{image.RepoTags.Count - 1}" : string.Empty;
                                                    var used = image.Containers >= 0 ? image.Containers : containers.Count(x => x.ImageID == image.ID);
                                                    var age = DateTime.UtcNow - image.Created;

                                                    var cells = new List<(string, Color?)>
                                                {
                                                    ("  ", null),
                                                    (UI.Crop(repository + more, repoWidth - 1).PadRight(repoWidth), dangling ? Color.Grey35 : Color.MediumPurple2),
                                                    (UI.Crop(tag, tagWidth - 1).PadRight(tagWidth), dangling ? Color.Grey35 : Color.Grey)
                                                };

                                                    if (showId)
                                                        cells.Add((image.ID[(image.ID.IndexOf(':') + 1)..][..12].PadRight(idWidth), Color.DarkOrange3));

                                                    cells.Add((UI.Size(image.Size).PadLeft(sizeWidth - 3) + "   ", Color.Grey35));

                                                    if (showCreated)
                                                        cells.Add(((age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
                                                            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
                                                            : $"{(int)age.TotalDays}d").PadRight(createdWidth), Color.CadetBlue));

                                                    cells.Add((used.ToString().PadLeft(usedWidth), used > 0 ? Color.Grey : Color.Grey35));

                                                    page.Add(new Markup(UI.Compose([.. cells], body, i == selected)));

                                                    drawn++;
                                                }

                                                break;
                                            }
                                        case Models.MainListSections.Volumes:
                                            {
                                                var showCreated = width >= 100;

                                                const int driverWidth = 10;
                                                var createdWidth = showCreated ? 12 : 0;
                                                const int mountedWidth = 30;
                                                var nameWidth = Math.Max(16, body - 2 - driverWidth - mountedWidth - createdWidth);

                                                page.Add(new Markup(UI.Compose(
                                                    [
                                                        ("  ", null),
                                                    Title(0, nameWidth, false),
                                                    Title(1, driverWidth, false),
                                                    Title(2, mountedWidth, false),
                                                    (showCreated ? Title(3, createdWidth, false) : (string.Empty, (Color?)null))
                                                    ], body, false)));

                                                if (volumesVisible.Count == 0)
                                                {
                                                    page.Add(new Markup($"[{Color.Grey}]No volumes on this engine.[/]"));

                                                    drawn = 1;
                                                    break;
                                                }

                                                var from = selected / bodyHeight * bodyHeight;
                                                var to = Math.Min(from + bodyHeight, volumesVisible.Count);

                                                for (int i = from; i < to; i++)
                                                {
                                                    var volume = volumesVisible[i];

                                                    var mounted = containers
                                                        .Where(x => (x.Mounts ?? []).Any(m => m.Name == volume.Name))
                                                        .Select(x => x.Names[0].TrimStart('/'))
                                                        .ToList();

                                                    var age = DateTime.TryParse(volume.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created)
                                                        ? DateTime.UtcNow - created.ToUniversalTime()
                                                        : TimeSpan.Zero;

                                                    var cells = new List<(string, Color?)>
                                                {
                                                    ("  ", null),
                                                    (UI.Crop(volume.Name, nameWidth - 1).PadRight(nameWidth), Color.Yellow),
                                                    (UI.Crop(volume.Driver, driverWidth - 1).PadRight(driverWidth), Color.Grey35),
                                                    (UI.Crop(mounted.Count > 0 ? string.Join(", ", mounted) : "–", mountedWidth - 1).PadRight(mountedWidth), mounted.Count > 0 ? Color.SteelBlue1 : Color.Grey35)
                                                };

                                                    if (showCreated)
                                                        cells.Add(((age == TimeSpan.Zero
                                                            ? "–"
                                                            : age.TotalHours < 1
                                                                ? $"{(int)age.TotalMinutes}m"
                                                                : age.TotalDays < 1
                                                                    ? $"{(int)age.TotalHours}h"
                                                                    : $"{(int)age.TotalDays}d").PadRight(createdWidth), Color.CadetBlue));

                                                    page.Add(new Markup(UI.Compose([.. cells], body, i == selected)));

                                                    drawn++;
                                                }

                                                break;
                                            }
                                        case Models.MainListSections.Networks:
                                            {
                                                var showSubnet = width >= 100;

                                                const int driverWidth = 10;
                                                const int scopeWidth = 8;
                                                var subnetWidth = showSubnet ? 20 : 0;
                                                const int attachedWidth = 12;
                                                var nameWidth = Math.Max(16, body - 2 - driverWidth - scopeWidth - subnetWidth - attachedWidth);

                                                page.Add(new Markup(UI.Compose(
                                                    [
                                                    ("  ", null),
                                                Title(0, nameWidth, false),
                                                Title(1, driverWidth, false),
                                                ("SCOPE".PadRight(scopeWidth), Color.Grey35),
                                                (showSubnet ? Title(2, subnetWidth, false) : (string.Empty, (Color?)null)),
                                                Title(3, attachedWidth, true)
                                                    ], body, false)));

                                                if (networksVisible.Count == 0)
                                                {
                                                    page.Add(new Markup($"[{Color.Grey}]No networks on this engine.[/]"));

                                                    drawn = 1;
                                                    break;
                                                }

                                                var from = selected / bodyHeight * bodyHeight;
                                                var to = Math.Min(from + bodyHeight, networksVisible.Count);

                                                for (int i = from; i < to; i++)
                                                {
                                                    var network = networksVisible[i];

                                                    var subnet = (network.IPAM?.Config ?? [])
                                                        .Select(x => x.Subnet)
                                                        .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "–";

                                                    var attached = containers
                                                        .Count(x => x.State is "running" or "paused" && (x.NetworkSettings?.Networks?.ContainsKey(network.Name) ?? false));

                                                    List<(string, Color?)> cells =
                                                    [
                                                        ("  ", null),
                                                    (UI.Crop(network.Name, nameWidth - 1).PadRight(nameWidth), Color.Aqua),
                                                    (UI.Crop(network.Driver ?? "–", driverWidth - 1).PadRight(driverWidth), Color.Grey35),
                                                    (UI.Crop(network.Scope ?? "–", scopeWidth - 1).PadRight(scopeWidth), Color.Grey35)
                                                    ];

                                                    if (showSubnet)
                                                        cells.Add((UI.Crop(subnet, subnetWidth - 1).PadRight(subnetWidth), subnet == "–"
                                                            ? Color.Grey35
                                                            : Color.CadetBlue));

                                                    cells.Add((attached.ToString().PadLeft(attachedWidth), attached > 0
                                                        ? Color.Grey
                                                        : Color.Grey35));

                                                    page.Add(new Markup(UI.Compose([.. cells], body, i == selected)));

                                                    drawn++;
                                                }

                                                break;
                                            }
                                        case Models.MainListSections.Events:
                                            {
                                                var showName = width >= 71;

                                                const int timeWidth = 10;
                                                const int typeWidth = 11;
                                                const int actionWidth = 22;
                                                var nameWidth = showName ? 22 : 0;
                                                var detailWidth = Math.Max(0, body - 2 - timeWidth - typeWidth - actionWidth - nameWidth);
                                                var showDetail = detailWidth > 2;

                                                page.Add(new Markup(UI.Compose([
                                                    ("  ", null),
                                                ($"TIME {(descending[section] ? '▼' : '▲')}".PadRight(timeWidth), Color.SteelBlue1),
                                                ("TYPE".PadRight(typeWidth), Color.Grey35),
                                                ("ACTION".PadRight(actionWidth), Color.Grey35),
                                                (showName ? "NAME".PadRight(nameWidth) : string.Empty, Color.Grey35),
                                                (showDetail ? "DETAIL".PadRight(detailWidth) : string.Empty, Color.Grey35)
                                                    ], body, false)));

                                                Message[] recent;

                                                lock (log)
                                                    recent = [..(paused ? log.Take(frozen) : log)
                                                    .Where(x => kind == 0 || x.Type == kinds[kind])
                                                    .Where(x => filter.Length == 0
                                                        || (x.Actor?.Attributes is { } a && a.TryGetValue("name", out var named) && named.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                                        || (x.Action ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase))
                                                    .TakeLast(bodyHeight)];

                                                if (descending[section])
                                                    Array.Reverse(recent);

                                                if (recent.Length == 0)
                                                {
                                                    page.Add(new Markup($"[{Color.Grey}]Waiting for engine events...[/]"));

                                                    drawn = 1;
                                                    break;
                                                }

                                                foreach (var message in recent)
                                                {
                                                    var attributes = message.Actor?.Attributes;

                                                    var name = attributes != null && attributes.TryGetValue("name", out var titled)
                                                        ? titled
                                                        : message.Actor?.ID is { Length: > 12 } id
                                                            ? id[..12]
                                                            : message.Actor?.ID ?? "–";

                                                    var action = message.Action ?? "–";

                                                    var detail = attributes == null
                                                        ? "–"
                                                        : action == "die" && attributes.TryGetValue("exitCode", out var code)
                                                            ? $"exit code {code}"
                                                            : attributes.TryGetValue("container", out var member)
                                                                ? member
                                                                : attributes.TryGetValue("image", out var image)
                                                                    ? image
                                                                    : "–";

                                                    var color = action switch
                                                    {
                                                        "die" or "destroy" or "kill" or "oom" => Color.Red3,
                                                        "start" or "create" or "connect" or "pull" => Color.Green3_1,
                                                        "stop" or "pause" or "restart" or "disconnect" => Color.Orange1,
                                                        _ when action.StartsWith("health_status") => action.EndsWith("unhealthy")
                                                            ? Color.Orange1
                                                            : Color.Green3_1,
                                                        _ => Color.Grey
                                                    };

                                                    page.Add(new Markup(UI.Compose(
                                                        [
                                                            ("  ", null),
                                                        (DateTimeOffset.FromUnixTimeMilliseconds(message.TimeNano / 1_000_000).ToLocalTime().ToString("HH:mm:ss").PadRight(timeWidth), Color.CadetBlue),
                                                        (UI.Crop(message.Type ?? "–", typeWidth - 1).PadRight(typeWidth), Color.Grey35),
                                                        (UI.Crop(action, actionWidth - 1).PadRight(actionWidth), color),
                                                        (showName ? UI.Crop(name, nameWidth - 1).PadRight(nameWidth) : string.Empty, Color.SteelBlue1),
                                                        (showDetail ? UI.Crop(detail, detailWidth - 1).PadRight(detailWidth) : string.Empty, Color.Grey)
                                                        ], body, false)));

                                                    drawn++;
                                                }

                                                break;
                                            }
                                        default:
                                            {
                                                page.Add(new Text(string.Empty));
                                                page.Add(new Markup($"[{Color.Grey.ToMarkup()}]{sections[section]} — not built yet[/]"));

                                                drawn = 1;
                                                break;
                                            }
                                    }

                                for (int i = drawn; i < bodyHeight; i++)
                                    page.Add(new Text(string.Empty));

                                if (notice is { } toast)
                                    page.Add(new Markup(UI.Toast(toast, body)));

                                page.Add(new Rule { Style = new Style(Color.Grey35) });
                                page.Add(new Markup(confirm != null
                                    ? UI.Spread(
                                    [
                                        (question, Color.Grey),
                                        ("[y] yes   [n] no", Color.SteelBlue1)
                                    ], body)
                                    : UI.Spread(section == (int)Models.MainListSections.Events
                                    ?
                                    [
                                        ("←→ section", Color.Grey),
                                        ($"f type: {kinds[kind]}", Color.Grey),
                                        (descending[section] ? "SHIFT+TAB newest first" : "SHIFT+TAB oldest first", Color.Grey),
                                        (paused ? "␣ resume" : "␣ pause", paused ? Color.Orange1 : Color.Grey),
                                        ("c clear", Color.Grey),
                                        ("/ filter", Color.Grey),
                                        ("? help", Color.Grey),
                                        ("q quit", Color.Grey)
                                    ]
                                    : section == (int)Models.MainListSections.Containers
                                    ?
                                    [
                                        ("↑↓ move", Color.Grey),
                                        ("←→ section", Color.Grey),
                                        ("⏎ details", Color.Grey),
                                        ("␣ start/stop", Color.Grey),
                                        ("p pause", Color.Grey),
                                        ("x remove", Color.Grey),
                                        ("Del prune", Color.Grey),
                                        (". actions", Color.Grey),
                                        ("/ filter", Color.Grey),
                                        ("? help", Color.Grey),
                                        ("q quit", Color.Grey)
                                    ]
                                    : section == (int)Models.MainListSections.Images
                                    ?
                                    [
                                        ("↑↓ move", Color.Grey),
                                        ("←→ section", Color.Grey),
                                        ("TAB sort", Color.Grey),
                                        ("⏎ details", Color.Grey),
                                        ("n run", Color.Grey),
                                        ("Del prune", Color.Grey),
                                        ("/ filter", Color.Grey),
                                        ("? help", Color.Grey),
                                        ("q quit", Color.Grey)
                                    ]
                                    :
                                    [
                                        ("↑↓ move", Color.Grey),
                                        ("←→ section", Color.Grey),
                                        ("TAB sort", Color.Grey),
                                        ("⏎ details", Color.Grey),
                                        ("Del prune", Color.Grey),
                                        ("/ filter", Color.Grey),
                                        ("? help", Color.Grey),
                                        ("q quit", Color.Grey)
                                    ], body)));

                                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                                ctx.Refresh();
                            }
                        });

                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("closing background work…", async _ =>
                        {
                            await cts.CancelAsync();

                            await events;

                            await stats;
                        });

                    AnsiConsole.Clear();

                    if (shell is { } run)
                    {
                        Console.CursorVisible = true;

                        AnsiConsole.MarkupLine($"[{Color.Grey35}]docker {Markup.Escape(string.Join(" ", run))}[/]");
                        AnsiConsole.WriteLine();

                        var info = new ProcessStartInfo("docker") { UseShellExecute = false };

                        foreach (var arg in run)
                            info.ArgumentList.Add(arg);

                        using var child = Process.Start(info);

                        if (child != null)
                            // ReSharper disable once MethodSupportsCancellation
                            await child.WaitForExitAsync();

                        Console.CursorVisible = false;

                        continue;
                    }

                    return;
                }
                catch (Exception ex) when (ex is DockerApiException or TimeoutException or OperationCanceledException or HttpRequestException or IOException or Win32Exception)
                {
                    await cts.CancelAsync();

                    await events;

                    await stats;

                    if (!await EngineDown.Display(ex))
                        return;
                }
            }

            (string, Color?) Title(int index, int width, bool right)
            {
                var label = index == sortBy[section]
                    ? $"{sorts[section][index]} {(descending[section] ? '▼' : '▲')}"
                    : sorts[section][index];

                return (right ? label.PadLeft(width) : label.PadRight(width), index == sortBy[section] ? Color.SteelBlue1 : Color.Grey35);
            }
        }
    }
}
