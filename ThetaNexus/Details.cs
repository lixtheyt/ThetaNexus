using Docker.DotNet;
using Docker.DotNet.Models;

using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ThetaNexus.Shared;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class Details
    {
        internal static async Task Display(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container, CancellationToken token)
        {
            var tabs = Enum.GetNames<Models.DetailsTabs>()
                .Select(x => x.ToLower())
                .ToArray();

            var section = 0;
            var hidden = true;
            var dirty = true;

            var inspect = await client.Containers.InspectContainerAsync(container.ID, token);
            var drivers = new Dictionary<string, string>();
            var refreshed = DateTime.UtcNow;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            return;
                        case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                            section = (section + tabs.Length - 1) % tabs.Length;
                            break;
                        case ConsoleKey.LeftArrow:
                            section = (section + tabs.Length - 1) % tabs.Length;
                            break;
                        case ConsoleKey.Tab:
                            section = (section + 1) % tabs.Length;
                            break;
                        case ConsoleKey.RightArrow:
                            section = (section + 1) % tabs.Length;
                            break;
                        case ConsoleKey.Spacebar:
                            await ContainerActions.StartStop(client, container, token);
                            break;
                        case ConsoleKey.S:
                            await DisplayStats(client, ctx, container, token);
                            break;
                        case ConsoleKey.V when section == 1:
                            hidden = !hidden;
                            break;
                        case ConsoleKey.L:
                            await DisplayLogs(client, ctx, container, token);
                            break;
                    }

                    dirty = true;
                    continue;
                }

                if (DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(1))
                {
                    refreshed = DateTime.UtcNow;
                    inspect = await client.Containers.InspectContainerAsync(container.ID, token);
                    dirty = true;

                    foreach (var attached in inspect.NetworkSettings.Networks.Keys)
                    {
                        try
                        {
                            drivers[attached] = (await client.Networks.InspectNetworkAsync(attached, token)).Driver;
                        }
                        catch (Exception)
                        {
                            drivers[attached] = "–";
                        }
                    }
                }

                if (!dirty)
                {
                    await Task.Delay(50);
                    continue;
                }

                dirty = false;

                var width = AnsiConsole.Profile.Width;
                var height = Console.WindowHeight;
                var body = width - 4;

                var (glyph, color) = ContainerActions.Pending(container.ID) is { } verb
                    ? ($"◌  {verb}", Color.Yellow)
                    : UI.Glyph(inspect);
                var name = container.Names[0].TrimStart('/');
                var id = container.ID[..12];

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.SteelBlue1.ToMarkup()}]{Markup.Escape(name)}[/]   [{Color.MediumPurple2.ToMarkup()}]{Markup.Escape(container.Image)}[/] [{Color.Grey35.ToMarkup()}]·[/] [{Color.DarkOrange3.ToMarkup()}]{id}[/]",
                            $"[{color.ToMarkup()}]{Markup.Escape(glyph.Replace("  ", " "))}[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Markup(UI.Spread([.. tabs.Select((x, i) => (i == section ? x.ToUpperInvariant() : x, i == section ? (Color?)Color.SteelBlue1 : Color.Grey35))], body)),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var bodyHeight = Math.Max(1, height - 9);

                Grid grid = new();

                var extra = 0;

                switch (section)
                {
                    case 0: //overview
                        {
                            var published = (inspect.NetworkSettings.Ports ?? new Dictionary<string, IList<PortBinding>>())
                                .Where(x => x.Value is not null)
                                .SelectMany(x => x.Value.Select(b => $"[{Color.Aqua}]{b.HostIP}:{b.HostPort}[/] [{Color.Grey35}]→[/] [{Color.Aqua}]{x.Key}[/]"))
                                .Distinct()
                                .ToList();

                            var ports = published.Count > 0 ? string.Join("   ", published) : $"[{Color.Grey35}]–[/]";

                            var project = inspect.Config.Labels is not null && inspect.Config.Labels.TryGetValue("com.docker.compose.project", out var compose)
                                ? $"[{Color.Khaki1}]{Markup.Escape(compose)}[/] [{Color.Grey35}]·[/] [{Color.Grey}]service[/] [{Color.Khaki1}]{Markup.Escape(inspect.Config.Labels.TryGetValue("com.docker.compose.service", out var service) ? service : "–")}[/]"
                                : $"[{Color.Grey35}]no project[/]";

                            var wide = body - 14;

                            var command = string.Join(" ", inspect.Config.Cmd ?? [])
                                .Split('\n')
                                .Select(x => string.Concat(x.Select(c => char.IsControl(c) ? ' ' : c)).Trim())
                                .Where(x => x.Length > 0)
                                .SelectMany(x => x.Chunk(wide).Select(c => new string(c)))
                                .ToArray();

                            var room = Math.Max(1, bodyHeight - 8);

                            extra = Math.Max(0, Math.Min(command.Length, room) - 1);

                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true })
                                .AddRow(new Markup($"[{Color.Grey}]Status[/]"), new Markup($"[{color}]{glyph}[/]")) // status
                                .AddRow(new Markup($"[{Color.Grey}]Created[/]"), new Markup($"[{Color.CadetBlue}]{inspect.Created.ToLocalTime():G}[/]")) // created
                                .AddRow(new Markup($"[{Color.Grey}]Started[/]"), new Markup($"[{Color.CadetBlue}]{DateTime.Parse(inspect.State.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime():G}[/]")) // started
                                .AddRow(new Markup($"[{Color.Grey}]Image[/]"), new Markup($"[{Color.MediumPurple2}]{Markup.Escape(inspect.Config.Image)}[/] [{Color.Grey35}]{Markup.Escape(inspect.Image[..19])}…[/]")) // image
                                .AddRow(new Markup($"[{Color.Grey}]Command[/]"), new Text(string.Join("\n", command.Take(room)) + (command.Length > room ? "…" : string.Empty))) // command
                                .AddRow(new Markup($"[{Color.Grey}]Ports[/]"), new Markup(ports)) // ports
                                .AddRow(new Markup($"[{Color.Grey}]Restarts[/]"), new Text(inspect.RestartCount.ToString())) // restarts
                                .AddRow(new Markup($"[{Color.Grey}]Limits[/]"), new Markup($"memory {(inspect.HostConfig.Memory > 0 ? $"{inspect.HostConfig.Memory / 1024 / 1024} MB" : "unlimited")} [{Color.Grey35}]·[/] cpus {(inspect.HostConfig.NanoCPUs > 0 ? (inspect.HostConfig.NanoCPUs / 1_000_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) : "unlimited")}")) // limits
                                .AddRow(new Markup($"[{Color.Grey}]Project[/]"), new Markup(project)); // project
                            break;
                        }
                    case 1: // env
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true });

                            foreach (var variable in inspect.Config.Env ?? [])
                            {
                                var pair = variable.Split('=', 2);
                                var value = pair.Length > 1 ? pair[1] : string.Empty;

                                grid.AddRow(new Markup($"[{Color.Grey}]{Markup.Escape(pair[0])}[/]"),
                                    new Markup($"[{(hidden ? Color.Grey35 : Color.Grey)}]{Markup.Escape(hidden ? new string('•', Math.Min(value.Length, body - 40)) : UI.Crop(value, body - 40))}[/]"));
                            }

                            grid.AddEmptyRow()
                                .AddRow(new Text(" "), new Markup($"[{Color.Grey35}]{(inspect.Config.Env?.Count ?? 0)} variables {(hidden ? "hidden" : "shown")}.  press [/][{Color.SteelBlue1}]v[/][{Color.Grey35}] to {(hidden ? "reveal" : "hide")}[/]"));

                            break;
                        }
                    case 2: // mounts
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true });
                            if (inspect.Mounts.Count > 0)
                                inspect.Mounts
                                    .OrderBy(x => x.Destination, StringComparer.Ordinal)
                                    .ToList()
                                    .ForEach(x => grid.AddRow(new Markup($"[{Color.Grey}]{Markup.Escape(x.Type)}[/]"), new Markup($"[{Color.Yellow3}]{x.Name}[/]  [{Color.Grey}]→[/]  [{Color.Grey}]{x.Destination}[/]    [{Color.Grey35}]{(x.RW ? "rw" : "ro")}[/]")));
                            else
                                grid.AddRow(new Text(""), new Markup($"[{Color.Grey}]This container has no mounts.[/]"));
                            break;
                        }
                    case 3: // networks
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true });

                            if (inspect.NetworkSettings.Networks is not { Count: > 0 })
                            {
                                grid.AddRow(new Text(" "), new Markup($"[{Color.Grey}]This container is not attached to any network.[/]"));
                                break;
                            }

                            foreach (var (attached, endpoint) in inspect.NetworkSettings.Networks)
                            {
                                grid.AddRow(new Markup($"[{Color.Green3_1}]{Markup.Escape(attached)}[/]"), new Text(drivers.TryGetValue(attached, out var driver) ? driver : "–"))
                                    .AddRow(new Markup($"[{Color.Grey}]  IPv4[/]"), new Text(string.IsNullOrEmpty(endpoint.IPAddress) ? "–" : $"{endpoint.IPAddress}/{endpoint.IPPrefixLen}"))
                                    .AddRow(new Markup($"[{Color.Grey}]  Gateway[/]"), new Text(string.IsNullOrEmpty(endpoint.Gateway) ? "–" : endpoint.Gateway))
                                    .AddRow(new Markup($"[{Color.Grey}]  Aliases[/]"), new Text(endpoint.Aliases is { Count: > 0 } ? string.Join(", ", endpoint.Aliases) : "–"))
                                    .AddEmptyRow();
                            }

                            break;
                        }
                    case 4: // health
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true });

                            if (inspect.Config.Healthcheck is not { } check)
                            {
                                grid.AddRow(new Text(" "), new Markup($"[{Color.Grey}]This image defines no healthcheck.[/]"));
                                break;
                            }

                            var (status, statusColor) = inspect.State.Health?.Status switch
                            {
                                "healthy" => ("●  healthy", Color.Green3_1),
                                "unhealthy" => ("●  unhealthy", Color.Orange1),
                                "starting" => ("●  starting", Color.Yellow),
                                _ => ("●  unknown", Color.Grey35)
                            };

                            grid.AddRow(new Markup($"[{Color.Grey}]Health[/]"), inspect.State.Health is { } health
                                    ? new Markup($"[{statusColor}]{status}[/]   [{Color.Grey}]·[/]    [{Color.Grey35}]failing streak {health.FailingStreak}[/]")
                                    : new Markup($"[{Color.Grey35}]not run yet[/]"))
                                .AddRow(new Markup($"[{Color.Grey}]Test[/]"), new Text(string.Join(" ", check.Test ?? [])))
                                .AddRow(new Markup($"[{Color.Grey}]Schedule[/]"), new Text($"every {(check.Interval > TimeSpan.Zero ? $"{check.Interval.TotalSeconds}s" : "default")} · timeout {(check.Timeout > TimeSpan.Zero ? $"{check.Timeout.TotalSeconds}s" : "default")} · retries {(check.Retries > 0 ? check.Retries.ToString() : "default")} · start period {check.StartPeriod / 1_000_000_000}s"))
                                .AddEmptyRow();

                            if (inspect.State.Health?.Log is { Count: > 0 } log)
                            {
                                foreach (var entry in log.Reverse())
                                    grid.AddRow(new Markup($"[{Color.Grey35}]{entry.Start.ToLocalTime():HH:mm:ss}[/]"), new Markup(UI.Compose(
                                        [
                                            (entry.ExitCode == 0
                                                ? ("●  0".PadRight(8), Color.Green3_1)
                                                : ($"✗  {entry.ExitCode}".PadRight(8), Color.Red3)),
                                            ($"{(entry.End - entry.Start).TotalMilliseconds:0} ms".PadRight(10), Color.Grey35),
                                            (UI.Crop(entry.Output ?? "", body - 34), Color.Grey)
                                        ], body - 12, false
                                        )));
                            }
                            else
                                grid.AddRow(new Text(" "), new Markup($"[{Color.Grey}]No probes recorded yet.[/]"));

                            break;
                        }
                }

                page.Add(grid);

                for (int i = grid.Rows.Count + extra; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                page.Add(new Rule { Style = new Style(Color.Grey) });
                page.Add(new Markup(
                    section == 1
                    ? UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("TAB tab", Color.Grey),
                        ("⏎ raw JSON", Color.Grey),
                        ("l logs", Color.Grey),
                        ("s stats", Color.Grey),
                        ("e shell", Color.Grey),
                        ("v values", Color.Grey),
                        ("␣ start/stop", Color.Grey)
                    ], body)
                    : UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("TAB tab", Color.Grey),
                        ("⏎ raw JSON", Color.Grey),
                        ("l logs", Color.Grey),
                        ("s stats", Color.Grey),
                        ("e shell", Color.Grey),
                        ("␣ start/stop", Color.Grey)
                    ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }

        internal static async Task DisplayStats(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container, CancellationToken token)
        {
            var latest = (ContainerStatsResponse?)null;
            var dirty = true;

            _ = client.Containers.GetContainerStatsAsync(container.ID,
                new ContainerStatsParameters { Stream = true },
                new Progress<ContainerStatsResponse>(x =>
                {
                    latest = x;
                    dirty = true;
                }),
                token);

            var history = new Queue<double>();
            var seen = default(DateTime);
            var last = (Rx: 0L, Tx: 0L);
            var rate = (Down: (double?)null, Up: (double?)null);

            var inspect = await client.Containers.InspectContainerAsync(container.ID, token);
            var refreshed = DateTime.UtcNow;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            return;

                        case ConsoleKey.C:
                            history.Clear();
                            break;

                        case ConsoleKey.Spacebar:
                            await ContainerActions.StartStop(client, container, token);
                            break;
                    }

                    dirty = true;
                    continue;
                }

                if (DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(1))
                {
                    refreshed = DateTime.UtcNow;
                    inspect = await client.Containers.InspectContainerAsync(container.ID, token);
                    dirty = true;
                }

                if (!dirty)
                {
                    await Task.Delay(50);
                    continue;
                }

                dirty = false;

                var width = AnsiConsole.Profile.Width;
                var height = Console.WindowHeight;
                var body = width - 4;

                var stats = latest;

                if (stats is not null && stats.Read != seen)
                {
                    seen = stats.Read;

                    var cpuDelta = (double)stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
                    var systemDelta = (double)stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
                    var cpus = stats.CPUStats.OnlineCPUs > 0 ? stats.CPUStats.OnlineCPUs : 1;

                    history.Enqueue(systemDelta > 0 && cpuDelta > 0
                        ? cpuDelta / systemDelta * cpus * 100.0
                        : 0);

                    while (history.Count > 40)
                        history.Dequeue();

                    var seconds = (stats.Read - stats.PreRead).TotalSeconds;
                    var rx = stats.Networks?.Values.Sum(x => (long)x.RxBytes) ?? 0;
                    var tx = stats.Networks?.Values.Sum(x => (long)x.TxBytes) ?? 0;

                    rate = seconds > 0 && last.Rx > 0
                        ? ((rx - last.Rx) / seconds, (tx - last.Tx) / seconds)
                        : (null, null);

                    last = (rx, tx);
                }

                var cpu = history.Count > 0 ? history.Last() : (double?)null;
                var scale = history.Count > 0 ? Math.Max(1.0, history.Max()) : 1.0;
                var spark = string.Concat(history.Select(x => "▁▂▃▄▅▆▇█"[(int)Math.Clamp(x / scale * 7, 0, 7)]));

                var cache = stats?.MemoryStats.Stats is not null && stats.MemoryStats.Stats.TryGetValue("inactive_file", out var inactive) ? inactive : 0;
                var used = stats is null ? 0 : (long)(stats.MemoryStats.Usage - cache);
                var limit = stats is null ? 0 : (long)stats.MemoryStats.Limit;
                var share = limit > 0 ? Math.Clamp(used / (double)limit, 0, 1) : 0;
                var filled = (int)Math.Round(30 * share);

                var (glyph, color) = ContainerActions.Pending(container.ID) is { } verb
                    ? ($"◌  {verb}", Color.Yellow)
                    : UI.Glyph(inspect);

                string Rate(double? bytes) => bytes is null ? "–" : bytes < 1024 ? $"{bytes:0} B/s" : $"{bytes / 1024:0.0} kB/s";

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.SteelBlue1}]{Markup.Escape(container.Names[0].TrimStart('/'))}[/]   [{Color.MediumPurple2}]{Markup.Escape(container.Image)}[/] [{Color.Grey35}]·[/] [{Color.DarkOrange3}]{container.ID[..12]}[/]",
                            $"[{color}]{Markup.Escape(glyph.Replace("  ", " "))}[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow($"[bold {Color.SteelBlue1}]STATS[/]", $"[{Color.Grey35}]{history.Count} samples[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty),

                    new Markup(UI.Compose(
                    [
                        ("CPU".PadRight(8), Color.Grey),
                        ((cpu is null ? "–" : $"{cpu:0.0}%").PadRight(9), Color.Grey),
                        (spark.PadRight(42), Color.Green3_1)
                    ], body, false)),

                    new Markup(UI.Compose(
                    [
                        ("MEM".PadRight(8), Color.Grey),
                        ($"{used / 1024.0 / 1024.0:0.0} MB / {limit / 1024 / 1024} MB".PadRight(20), Color.Grey),
                        (new string('█', filled), Color.CadetBlue),
                        (new string('░', 30 - filled) + "  ", Color.Grey35),
                        ($"{share * 100:0.0}%", Color.Grey)
                    ], body, false)),

                    new Markup(UI.Compose(
                    [
                        ("NET".PadRight(8), Color.Grey),
                        ($"↓ {Rate(rate.Down)}".PadRight(15), Color.Aqua),
                        ($"↑ {Rate(rate.Up)}".PadRight(18), Color.Aqua),
                        ($"total ↓ {last.Rx} B  ↑ {last.Tx} B", Color.Grey35)
                    ], body, false)),

                    new Markup(UI.Compose(
                    [
                        ("PIDS".PadRight(8), Color.Grey),
                        (stats?.PidsStats?.Current.ToString() ?? "–", Color.Grey)
                    ], body, false))
                };

                for (int i = page.Count; i < height - 4; i++)
                    page.Add(new Text(string.Empty));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(UI.Spread(
                [
                    ("ESC back", Color.Grey),
                    ("c clear graph", Color.Grey),
                    ("␣ start/stop", Color.Grey)
                ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }

        internal static async Task DisplayLogs(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var chars = new char[buffer.Length];

            var dirty = true;
            var refreshed = DateTime.UtcNow;

            var offset = 0;
            var visible = 0;

            List<(bool Error, string Text)> logs = new();
            string[] partials = ["", ""];

            var decoders = new Decoder[2]
            {
                Encoding.UTF8.GetDecoder(), // stdout
                Encoding.UTF8.GetDecoder() // stderr
            };

            var inspect = await client.Containers.InspectContainerAsync(container.ID, token);

            using var stream = await client.Containers.GetContainerLogsAsync(container.ID, inspect.Config.Tty, new ContainerLogsParameters
            {
                Follow = false,
                ShowStdout = true,
                ShowStderr = true,
                Timestamps = false,
                Tail = "500"
            }, token);

            while (true)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, token);

                if (read.EOF)
                    break;

                int i = read.Target == MultiplexedStream.TargetStream.StandardError
                    ? 1
                    : 0;

                var charsNumber = decoders[i].GetChars(buffer, 0, read.Count, chars, 0);

                var parts = (partials[i] + new string(chars, 0, charsNumber)).Split('\n');

                foreach (var line in parts[..^1])
                    logs.Add((i == 1, line.TrimEnd('\r')));

                partials[i] = parts[^1];
            }

            for (int i = 0; i < partials.Length; i++)
                if (partials[i].Length > 0)
                    logs.Add((i == 1, partials[i]));

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            return;
                        case ConsoleKey.UpArrow:
                            offset++;
                            break;
                        case ConsoleKey.DownArrow:
                            offset--;
                            break;
                        case ConsoleKey.PageUp:
                            offset += Math.Max(1, visible);
                            break;
                        case ConsoleKey.PageDown:
                            offset -= Math.Max(1, visible);
                            break;
                        case ConsoleKey.Home:
                            offset = logs.Count;
                            break;
                        case ConsoleKey.End:
                            offset = 0;
                            break;
                    }

                    dirty = true;
                    continue;
                }

                if (DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(1))
                {
                    refreshed = DateTime.UtcNow;
                    inspect = await client.Containers.InspectContainerAsync(container.ID, token);
                    dirty = true;
                }

                if (!dirty)
                {
                    await Task.Delay(50);
                    continue;
                }

                dirty = false;

                var width = AnsiConsole.Profile.Width;
                var height = Console.WindowHeight;
                var body = width - 4;
                var bodyHeight = Math.Max(1, height - 9);

                visible = bodyHeight;
                offset = Math.Clamp(offset, 0, Math.Max(0, logs.Count - bodyHeight));

                var (glyph, color) = ContainerActions.Pending(container.ID) is { } verb
                    ? ($"◌  {verb}", Color.Yellow)
                    : UI.Glyph(inspect);

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.SteelBlue1}]{Markup.Escape(container.Names[0].TrimStart('/'))}[/]   [{Color.MediumPurple2}]{Markup.Escape(container.Image)}[/] [{Color.Grey35}]·[/] [{Color.DarkOrange3}]{container.ID[..12]}[/]",
                            $"[{color}]{Markup.Escape(glyph.Replace("  ", " "))}[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]LOGS[/]",
                            $"[{Color.Grey35}]{logs.Count} lines[/] [{Color.Grey35}]·[/] [{Color.Grey35}]{logs.Count(x => x.Error)} stderr[/]{(offset > 0 ? $" [{Color.Grey35}]·[/] [{Color.SteelBlue1}]▼ {offset}[/]" : string.Empty)}"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var start = Math.Max(0, logs.Count - bodyHeight - offset);
                var tail = logs.Skip(start).Take(bodyHeight).ToArray();
                var digits = logs.Count.ToString().Length;

                for (int i = 0; i < tail.Length; i++)
                {
                    var (error, text) = tail[i];

                    page.Add(new Markup(UI.Compose(
                    [
                        ((start + i + 1).ToString().PadLeft(digits) + "  ", Color.CadetBlue),
                        (error ? "!  " : "   ", Color.Red3),
                        (UI.Crop(Regex.Replace(text, "\u001b\\[[0-9;?]*[@-~]", string.Empty), body - digits - 5), Color.Grey)
                    ], body, false)));
                }

                for (int i = tail.Length; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(UI.Spread(
                [
                    ("ESC back", Color.Grey),
                    ("↑↓ scroll", Color.Grey),
                    ("PgUp/PgDn page", Color.Grey),
                    ("home/end jump", Color.Grey)
                ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }

        internal static async Task MoreActions(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container)
        {

        }
    }
}