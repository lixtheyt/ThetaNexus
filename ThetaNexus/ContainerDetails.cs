using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ThetaNexus.Shared;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class ContainerDetails
    {
        internal static async Task<string[]?> Display(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container, CancellationToken token)
        {
            var tabs = Enum.GetNames<Models.DetailsTabs>()
                .Select(x => x.ToLower())
                .ToArray();

            var section = 0;
            var hidden = true;
            var path = "/";
            var cursor = 0;
            string[] listing = [];
            var listed = string.Empty;
            Task<string[]>? loading = null;
            var dirty = true;

            var menu = false;
            var chosen = 0;
            var confirm = false;
            ConsoleKey? picked = null;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;

            var inspect = await client.Containers.InspectContainerAsync(container.ID, token);
            var drivers = new Dictionary<string, string>();
            var refreshed = DateTime.UtcNow;

            while (true)
            {
                var actions = ContainerActions.Applicable(container, inspect.State.Status);

                if (menu && picked is { } want && Array.FindIndex(actions, x => x.Key == want) is var found && found >= 0)
                    chosen = found;

                chosen = Math.Clamp(chosen, 0, Math.Max(0, actions.Length - 1));

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    var pressed = key.Key;

                    notice = null;
                    noticed = DateTime.UtcNow;

                    if (confirm)
                    {
                        if (key.Key == ConsoleKey.Y)
                            await ContainerActions.Remove(client, container, token);

                        confirm = false;
                        dirty = true;
                        continue;
                    }

                    if (menu)
                    {
                        menu = key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow;

                        if (key.Key == ConsoleKey.UpArrow)
                            chosen = Math.Max(0, chosen - 1);

                        if (key.Key == ConsoleKey.DownArrow)
                            chosen = Math.Min(actions.Length - 1, chosen + 1);

                        picked = actions.Length > 0 ? actions[chosen].Key : null;

                        dirty = true;

                        if (menu || key.Key != ConsoleKey.Enter)
                            continue;

                        pressed = actions[chosen].Key;
                    }

                    switch (pressed)
                    {
                        case ConsoleKey.Escape:
                            return null;
                        case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                        case ConsoleKey.LeftArrow:
                            section = (section + tabs.Length - 1) % tabs.Length;
                            break;
                        case ConsoleKey.Tab:
                        case ConsoleKey.RightArrow:
                            section = (section + 1) % tabs.Length;
                            break;
                        case ConsoleKey.Spacebar:
                            await ContainerActions.StartStop(client, container, inspect.State.Status, token);
                            break;
                        case ConsoleKey.S:
                            await ContainerStats.Display(client, ctx, container, token);
                            break;
                        case ConsoleKey.V when section == (int)Models.DetailsTabs.Env:
                            hidden = !hidden;
                            break;
                        case ConsoleKey.L:
                            await DisplayLogs(client, ctx, container, token);
                            break;
                        case ConsoleKey.E when inspect.State.Running:
                            return ["exec", "-it", container.ID, "sh", "-c", "command -v bash >/dev/null && exec bash || exec sh"];
                        case ConsoleKey.UpArrow when section == (int)Models.DetailsTabs.Files:
                            cursor = Math.Max(0, cursor - 1);
                            break;
                        case ConsoleKey.DownArrow when section == (int)Models.DetailsTabs.Files:
                            cursor = Math.Min(listing.Length - 1, cursor + 1);
                            break;
                        case ConsoleKey.Enter when section == (int)Models.DetailsTabs.Files && cursor < listing.Length:
                            {
                                var parts = listing[cursor].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                                if (parts.Length >= 9 && parts[0].StartsWith('d') && parts[^1] != ".")
                                {
                                    path = parts[^1] == ".."
                                        ? path.TrimEnd('/')[..(path.TrimEnd('/').LastIndexOf('/') + 1)]
                                        : path.TrimEnd('/') + "/" + parts[^1];

                                    if (path.Length == 0)
                                        path = "/";

                                    cursor = 0;
                                    listed = string.Empty;
                                }

                                break;
                            }
                        case ConsoleKey.Backspace when section == (int)Models.DetailsTabs.Files && path != "/":
                            path = path.TrimEnd('/')[..(path.TrimEnd('/').LastIndexOf('/') + 1)];
                            cursor = 0;
                            listed = string.Empty;
                            break;
                        case ConsoleKey.Enter:
                            await RawJson.DisplayJson(ctx,
                                $"[{Color.SteelBlue1}]{Markup.Escape(container.Names[0].TrimStart('/'))}[/]   [{Color.MediumPurple2}]{Markup.Escape(container.Image)}[/] [{Color.Grey35}]·[/] [{Color.DarkOrange3}]{container.ID[..12]}[/]",
                                ["inspect", container.ID], token);
                            break;
                        case var _ when key.KeyChar == '?':
                            await Help.Display(ctx, token);
                            break;
                        case ConsoleKey.R:
                            await ContainerActions.Restart(client, container, token);
                            break;
                        case ConsoleKey.P:
                            await ContainerActions.Pause(client, container, inspect.State.Status, token);
                            break;
                        case ConsoleKey.K:
                            await ContainerActions.Kill(client, container, token);
                            break;
                        case ConsoleKey.X:
                            confirm = true;
                            break;
                        case var _ when key.KeyChar == '.' && actions.Length > 0:
                            menu = true;
                            chosen = 0;
                            picked = actions[0].Key;
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

                if (section == (int)Models.DetailsTabs.Files && listed != path && inspect.State.Running && loading == null)
                {
                    listed = path;
                    loading = Listing(client, container.ID, path, token);
                }

                if (loading != null)
                {
                    if (loading.IsCompleted)
                    {
                        try
                        {
                            listing = await loading;
                        }
                        catch (Exception)
                        {
                            listing = [];
                        }

                        loading = null;
                    }

                    dirty = true;
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
                    await Task.Delay(50, token);
                    continue;
                }

                dirty = false;

                var width = AnsiConsole.Profile.Width;
                var height = Console.WindowHeight;
                var body = width - 4;
                var bodyHeight = Math.Max(1, height - 9 - (notice == null ? 0 : 1));

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
                    new Markup(UI.Spread([..tabs.Select((x, i) => (i == section ? x.ToUpperInvariant() : x, i == section ? (Color?)Color.SteelBlue1 : Color.Grey35))], body)),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                Grid grid = new();

                var extra = 0;

                switch ((Models.DetailsTabs)section)
                {
                    case Models.DetailsTabs.Overview:
                        {
                            var published = (inspect.NetworkSettings.Ports ?? new Dictionary<string, IList<PortBinding>>())
                                .Where(x => x.Value != null)
                                .SelectMany(x => x.Value.Select(b => $"[{Color.Aqua}]{b.HostIP}:{b.HostPort}[/] [{Color.Grey35}]→[/] [{Color.Aqua}]{x.Key}[/]"))
                                .Distinct()
                                .ToList();

                            var ports = published.Count > 0 ? string.Join("   ", published) : $"[{Color.Grey35}]–[/]";

                            var project = inspect.Config.Labels != null && inspect.Config.Labels.TryGetValue("com.docker.compose.project", out var compose)
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
                    case Models.DetailsTabs.Env:
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
                    case Models.DetailsTabs.Mounts:
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
                    case Models.DetailsTabs.Networks:
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
                    case Models.DetailsTabs.Health:
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
                    case Models.DetailsTabs.Files:
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 2, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true });

                            if (!inspect.State.Running)
                            {
                                grid.AddRow(new Text(""), new Markup($"[{Color.Grey}]The container is not running, so its files cannot be listed.[/]"));
                                break;
                            }

                            grid.AddRow(new Text(" "), new Markup(UI.Compose(
                            [
                                ("path".PadRight(12), Color.Grey),
                                (UI.Crop(path, Math.Max(8, body - 20)), Color.Khaki1)
                            ], body - 4, false)))
                                .AddEmptyRow();
                            
                            cursor = Math.Clamp(cursor, 0, Math.Max(0, listing.Length - 1));

                            if (loading != null)
                            {
                                grid.AddRow(new Text(" "), new Markup($"[{Color.SteelBlue1}]{"⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[(int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 80 % 10)]}[/]  [{Color.Grey}]listing {Markup.Escape(UI.Crop(path, Math.Max(8, body - 20)))}[/]"));
                                break;
                            }

                            if (listing.Length == 0)
                            {
                                grid.AddRow(new Text(" "), new Markup($"[{Color.Grey}]empty[/]"));
                                break;
                            }

                            foreach (var (line, i) in listing.Select((x, i) => (x, i)).Skip(Math.Max(0, cursor - bodyHeight + 6)).Take(Math.Max(1, bodyHeight - 4)))
                            {
                                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                                if (parts.Length < 9)
                                    continue;

                                var folder = parts[0].StartsWith('d');

                                grid.AddRow(
                                    new Markup(i == cursor
                                        ? $"[{Color.SteelBlue1}]▸[/]"
                                        : " "),
                                    new Markup(UI.Compose(
                                    [
                                        (parts[0].PadRight(12), Color.Grey35),
                                        ((folder ? "-" : UI.Size(long.TryParse(parts[4], out var size)
                                            ? size
                                            : 0
                                            )).PadRight(9) + "   ", Color.Grey35),
                                        (string.Join(' ', parts[8..]), folder
                                            ? Color.SteelBlue1
                                            : Color.Grey)
                                    ], body - 4, i == cursor)));
                            }

                            break;
                        }
                }

                var drawn = grid.Rows.Count + extra;

                if (menu)
                {
                    page.Add(UI.Menu(
                        [
                            (name, Color.SteelBlue1),
                            (glyph.Replace("  ", " "), color)
                        ],
                        [.. actions.Select(x => (x.Shown, x.Label))],
                        chosen,
                        body));

                    drawn = actions.Length + 8;
                }
                else
                    page.Add(grid);

                for (int i = drawn; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(confirm
                    ? UI.Spread(
                    [
                        ($"Remove {Markup.Escape(container.Names[0].TrimStart('/'))} permanently?", Color.Grey),
                        ("[y] yes   [n] no", Color.SteelBlue1)
                    ], body)
                    : section == (int)Models.DetailsTabs.Files
                    ? UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("TAB/←→ tab", Color.Grey),
                        ("↑↓ move", Color.Grey),
                        ("⏎ enter", Color.Grey),
                        ("⌫  up", Color.Grey),
                        ("l logs", Color.Grey),
                        ("s stats", Color.Grey),
                        ("e shell", Color.Grey),
                        (". actions", Color.Grey),
                        ("? help", Color.Grey)
                    ], body)
                    : section == (int)Models.DetailsTabs.Env
                    ? UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("TAB/←→ tab", Color.Grey),
                        ("⏎ raw JSON", Color.Grey),
                        ("v values", Color.Grey),
                        ("l logs", Color.Grey),
                        ("s stats", Color.Grey),
                        ("e shell", Color.Grey),
                        ("␣ start/stop", Color.Grey),
                        (". actions", Color.Grey),
                        ("? help", Color.Grey)
                    ], body)
                    : UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("TAB/←→ tab", Color.Grey),
                        ("⏎ raw JSON", Color.Grey),
                        ("l logs", Color.Grey),
                        ("s stats", Color.Grey),
                        ("e shell", Color.Grey),
                        ("␣ start/stop", Color.Grey),
                        (". actions", Color.Grey),
                        ("? help", Color.Grey)
                    ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }

        private static async Task DisplayLogs(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var chars = new char[buffer.Length];

            var dirty = true;
            var refreshed = DateTime.UtcNow;

            var offset = 0;
            var visible = 0;
            var top = 0;

            var timestamps = false;
            var wrap = false;
            var tail = 2000;

            var search = string.Empty;
            var typed = string.Empty;
            var searching = false;
            var hit = 0;
            int? jumpTo = null;

            List<(bool Error, string Text)> logs = new();
            var incoming = new ConcurrentQueue<(bool Error, string Text)>();

            string[] partials = ["", ""];

            var decoders = new Decoder[2]
            {
                Encoding.UTF8.GetDecoder(), // stdout
                Encoding.UTF8.GetDecoder() // stderr
            };

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var inspect = await client.Containers.InspectContainerAsync(container.ID, cts.Token);

            CancellationTokenSource? feed = null;
            MultiplexedStream? stream = null;
            var reader = Task.CompletedTask;

            await Restart();

            while (true)
            {
                while (incoming.TryDequeue(out var line))
                {
                    logs.Add(line);
                    dirty = true;

                    if (offset > 0)
                        offset++;
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    notice = null;

                    if (searching)
                    {
                        switch (key.Key)
                        {
                            case ConsoleKey.Escape:
                                searching = false;
                                break;
                            case ConsoleKey.Enter:
                                {
                                    searching = false;
                                    search = typed.Trim();
                                    hit = 0;

                                    var found = search.Length == 0
                                        ? -1
                                        : logs.FindIndex(x => x.Text.Contains(search, StringComparison.OrdinalIgnoreCase));

                                    if (found >= 0)
                                        jumpTo = found;

                                    break;
                                }
                            case ConsoleKey.Backspace:
                                typed = typed.Length > 0 ? typed[..^1] : typed;
                                break;
                            default:
                                if (!char.IsControl(key.KeyChar))
                                    typed += key.KeyChar;

                                break;
                        }

                        dirty = true;
                        continue;
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            await Stop();
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
                            offset = int.MaxValue;
                            break;
                        case ConsoleKey.End:
                        case ConsoleKey.F:
                            offset = 0;
                            break;
                        case ConsoleKey.W:
                            wrap = !wrap;

                            if (offset > 0)
                                jumpTo = top;

                            break;
                        case ConsoleKey.T:
                            timestamps = !timestamps;
                            await Restart();
                            break;
                        case ConsoleKey.Add:
                        case ConsoleKey.OemPlus:
                            tail = Math.Min(20000, tail * 2);
                            await Restart();
                            break;
                        case ConsoleKey.Subtract:
                        case ConsoleKey.OemMinus:
                            tail = Math.Max(100, tail / 2);
                            await Restart();
                            break;
                        case ConsoleKey.C:
                            logs.Clear();
                            offset = 0;
                            break;
                        case ConsoleKey.S when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                            {
                                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                                var file = Path.Combine(Directory.Exists(downloads) ? downloads : Environment.CurrentDirectory,
                                    $"{container.Names[0].TrimStart('/')}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                                try
                                {
                                    await File.WriteAllLinesAsync(file, logs.Select(x => (x.Error ? "! " : "  ") + x.Text), cts.Token);

                                    notice = ($"saved {logs.Count} lines to {file}", Models.Outcome.Succeeded);
                                }
                                catch (Exception failure)
                                {
                                    notice = (failure.Message, Models.Outcome.Failed);
                                }

                                noticed = DateTime.UtcNow;
                                break;
                            }
                        case ConsoleKey.N when search.Length > 0:
                            {
                                var matches = logs.Select((x, i) => (x, i))
                                    .Where(x => x.x.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
                                    .Select(x => x.i)
                                    .ToArray();

                                if (matches.Length > 0)
                                {
                                    hit = key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                                        ? (hit + matches.Length - 1) % matches.Length
                                        : (hit + 1) % matches.Length;

                                    jumpTo = matches[hit];
                                }

                                break;
                            }
                        case var _ when key.KeyChar == '/':
                            searching = true;
                            typed = search;
                            break;
                    }

                    dirty = true;
                    continue;
                }

                if (DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(1))
                {
                    refreshed = DateTime.UtcNow;
                    inspect = await client.Containers.InspectContainerAsync(container.ID, cts.Token);
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

                var digits = Math.Max(1, logs.Count).ToString().Length;
                var room = Math.Max(8, body - digits - 5);

                List<(int Line, bool Error, string Text, bool First)> rows = [];

                for (int i = 0; i < logs.Count; i++)
                {
                    var clean = Regex.Replace(logs[i].Text, "\\[[0-9;?]*[@-~]", string.Empty);

                    if (!wrap)
                    {
                        rows.Add((i, logs[i].Error, UI.Crop(clean, room), true));
                        continue;
                    }
                   
                    if (clean.Length == 0)
                    {
                        rows.Add((i, logs[i].Error, string.Empty, true));
                        continue;
                    }

                    var chunks = clean.Chunk(room).Select(x => new string(x)).ToArray();

                    for (int c = 0; c < chunks.Length; c++)
                        rows.Add((i, logs[i].Error, chunks[c], c == 0));
                }

                if (jumpTo is { } wanted)
                {
                    var target = rows.FindIndex(x => x.Line == wanted && x.First);

                    if (target >= 0)
                        offset = rows.Count - bodyHeight - target + bodyHeight / 2;

                    jumpTo = null;
                }

                visible = bodyHeight;
                offset = Math.Clamp(offset, 0, Math.Max(0, rows.Count - bodyHeight));

                var hits = search.Length == 0
                    ? 0
                    : logs.Count(x => x.Text.Contains(search, StringComparison.OrdinalIgnoreCase));

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
                            $"[{(offset == 0 ? Color.Green3_1 : Color.Grey35)}]follow {(offset == 0 ? "ON" : "OFF")}[/] [{Color.Grey35}]·[/] [{Color.Grey35}]{logs.Count} lines[/] [{Color.Grey35}]·[/] [{Color.Grey35}]{logs.Count(x => x.Error)} stderr[/] [{Color.Grey35}]·[/] [{Color.Grey35}]tail {tail}[/]"
                                + (timestamps ? $" [{Color.Grey35}]·[/] [{Color.CadetBlue}]times[/]" : string.Empty)
                                + (wrap ? $" [{Color.Grey35}]·[/] [{Color.CadetBlue}]wrap[/]" : string.Empty)
                                + (search.Length > 0 ? $" [{Color.Grey35}]·[/] [{Color.Khaki1}]/{Markup.Escape(search)}[/] [{Color.Grey35}]{(hits == 0 ? "no match" : $"{hit + 1} of {hits}")}[/]" : string.Empty)
                                + (offset > 0 ? $" [{Color.Grey35}]·[/] [{Color.SteelBlue1}]▼ {offset}[/]" : string.Empty)
                                + (reader.IsCompleted ? $" [{Color.Grey35}]·[/] [{Color.Orange1}]stream ended[/]" : string.Empty)),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var start = Math.Max(0, rows.Count - bodyHeight - offset);
                var window = rows.Skip(start).Take(bodyHeight).ToArray();

                top = window.Length > 0 ? window[0].Line : 0;

                foreach (var (number, error, text, first) in window)
                {
                    var at = search.Length == 0
                        ? -1
                        : text.IndexOf(search, StringComparison.OrdinalIgnoreCase);

                    page.Add(new Markup(UI.Compose(
                    [
                        ((first ? (number + 1).ToString() : string.Empty).PadLeft(digits) + "  ", Color.CadetBlue),
                        (error ? "!  " : "   ", Color.Red3),
                        .. at < 0
                            ? new (string, Color?)[] { (text, Color.Grey) }
                            :
                            [
                                (text[..at], Color.Grey),
                                (text.Substring(at, search.Length), Color.Khaki1),
                                (text[(at + search.Length)..], Color.Grey)
                            ]
                    ], body, false)));
                }

                for (int i = window.Length; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(searching
                    ? UI.Spread([($"Search: {typed}_", Color.SteelBlue1)], body)
                    : UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("↑↓ scroll", Color.Grey),
                        ("f follow", Color.Grey),
                        ("/ search", Color.Grey),
                        ("n/N next", search.Length > 0 ? Color.Khaki1 : Color.Grey35),
                        ("w wrap", wrap ? Color.CadetBlue : Color.Grey),
                        ("t times", timestamps ? Color.CadetBlue : Color.Grey),
                        ("+/- tail", Color.Grey),
                        ("c clear", Color.Grey),
                        ("S save", Color.Grey)
                    ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }

            async Task Stop()
            {
                if (feed != null)
                {
                    await feed.CancelAsync();

                    try
                    {
                        await reader;
                    }
                    catch (OperationCanceledException)
                    {
                        // ignored
                    }

                    stream?.Dispose();
                    feed.Dispose();

                    feed = null;
                    stream = null;
                }
            }

            async Task Restart()
            {
                await Stop();

                logs.Clear();
                incoming.Clear();
                partials = ["", ""];
                decoders = [Encoding.UTF8.GetDecoder(), Encoding.UTF8.GetDecoder()];
                offset = 0;

                feed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                var feeding = feed.Token;
                
                stream = await client.Containers.GetContainerLogsAsync(container.ID, inspect.Config.Tty, new ContainerLogsParameters
                {
                    Follow = true,
                    ShowStdout = true,
                    ShowStderr = true,
                    Timestamps = timestamps,
                    Tail = tail.ToString()
                }, feeding);

                var reading = stream;
                
                reader = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            var read = await reading.ReadOutputAsync(buffer, 0, buffer.Length, feeding);

                            if (read.EOF)
                                break;

                            int i = read.Target == MultiplexedStream.TargetStream.StandardError
                                ? 1
                                : 0;

                            var charsNumber = decoders[i].GetChars(buffer, 0, read.Count, chars, 0);

                            var parts = (partials[i] + new string(chars, 0, charsNumber)).Split('\n');

                            foreach (var text in parts[..^1])
                                incoming.Enqueue((i == 1, text.TrimEnd('\r')));

                            partials[i] = parts[^1];
                        }

                        for (int i = 0; i < partials.Length; i++)
                            if (partials[i].Length > 0)
                                incoming.Enqueue((i == 1, partials[i]));
                    }
                    catch (OperationCanceledException)
                    {
                        // ignored
                    }
                    catch (ObjectDisposedException)
                    {
                        // ignored
                    }
                }, feeding);
            }
        }

        private static async Task<string[]> Listing(DockerClient client, string id, string path, CancellationToken token)
        {
            var exec = await client.Exec.ExecCreateContainerAsync(id, new ContainerExecCreateParameters
            {
                Cmd = ["ls", "-la", path],
                AttachStdout = true,
                AttachStderr = true
            }, token);

            using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, token);

            var buffer = new byte[16384];
            var text = new StringBuilder();

            while (true)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, token);

                if (result.EOF)
                    break;

                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }

            return [.. text.ToString()
                .Split('\n')
                .Select(x => x.TrimEnd('\r'))
                .Where(x => x.Length > 0 && !x.StartsWith("total "))];
        }
    }
}
