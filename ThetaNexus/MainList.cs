using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class MainList
    {
        internal static async Task Display(DockerClient client)
        {
            var sections = new[] { "containers", "images", "volumes", "networks", "events" };
            var columns = new[] { "NAME", "STATE", "IMAGE", "PORTS", "CPU", "MEM", "UP" };

            var collapsed = new HashSet<string>();
            var selected = 0;
            var section = 0;
            var sortBy = 0;
            var descending = false;

            while (true)
            {
                using var cts = new CancellationTokenSource();

                var events = Task.CompletedTask;

                try
                {
                    await client.System.PingAsync(cts.Token);

                    var version = await client.System.GetVersionAsync(cts.Token);

                    var engine = $"engine {version.Version}";

                    var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cts.Token);

                    var refreshed = DateTime.UtcNow;
                    var stale = false;
                    var dirty = true;

                    events = Task.Run(async () =>
                    {
                        try
                        {
                            await client.System.MonitorEventsAsync(new ContainerEventsParameters(), new Progress<Message>(_ => stale = true), cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    });

                    AnsiConsole.Clear();

                    await AnsiConsole.Live(new Markup(string.Empty))
                        .StartAsync(async ctx =>
                        {
                            while (true)
                            {
                                if (stale || DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(2))
                                {
                                    stale = false;
                                    refreshed = DateTime.UtcNow;
                                    containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cts.Token);
                                    dirty = true;
                                }

                                var groups = containers
                                    .GroupBy(x => x.Labels is not null && x.Labels.TryGetValue("com.docker.compose.project", out var project) ? project : "no project")
                                    .OrderBy(x => x.Key == "no project")
                                    .ThenBy(x => x.Key)
                                    .ToList();

                                var rows = new List<(string Project, ContainerListResponse? Container)>();

                                foreach (var group in groups)
                                {
                                    rows.Add((group.Key, null));

                                    if (collapsed.Contains(group.Key))
                                        continue;

                                    var ordered = sortBy switch
                                    {
                                        1 => group.OrderBy(x => x.State),
                                        2 => group.OrderBy(x => x.Image),
                                        3 => group.OrderBy(x => (x.Ports ?? []).Where(p => p.PublicPort > 0).Select(p => (int)p.PublicPort).DefaultIfEmpty(0).Max()),
                                        6 => group.OrderBy(x => x.Created),
                                        _ => group.OrderBy(x => x.Names[0])
                                    };

                                    rows.AddRange((descending ? ordered.Reverse() : ordered)
                                        .Select(x => (group.Key, (ContainerListResponse?)x)));
                                }

                                selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));

                                if (Console.KeyAvailable)
                                {
                                    var key = Console.ReadKey(intercept: true);

                                    switch (key.Key)
                                    {
                                        case ConsoleKey.UpArrow:
                                            selected = Math.Max(0, selected - 1);
                                            break;

                                        case ConsoleKey.DownArrow:
                                            selected = Math.Min(rows.Count - 1, selected + 1);
                                            break;

                                        case ConsoleKey.LeftArrow:
                                            section = (section + sections.Length - 1) % sections.Length;
                                            break;

                                        case ConsoleKey.RightArrow:
                                            section = (section + 1) % sections.Length;
                                            break;

                                        case >= ConsoleKey.D1 and <= ConsoleKey.D5:
                                            section = key.Key - ConsoleKey.D1;
                                            break;

                                        case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                            descending = !descending;
                                            break;

                                        case ConsoleKey.Tab:
                                            sortBy = (sortBy + 1) % columns.Length;
                                            break;

                                        case ConsoleKey.C when rows.Count > 0:
                                            var toToggle = rows[selected].Project;

                                            if (collapsed.Remove(toToggle))
                                                break;

                                            collapsed.Add(toToggle);
                                            selected = rows.FindIndex(x => x.Project == toToggle && x.Container is null);
                                            break;
                                    }

                                    dirty = true;
                                    continue;
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

                                var showImage = width >= 70;
                                var showPorts = width >= 90;
                                var showCpu = width >= 90;
                                var showMem = width >= 110;

                                var nameWidth = Math.Clamp(containers.Count == 0 ? 14 : containers.Max(x => x.Names[0].TrimStart('/').Length) + 2, 14, 28);
                                var stateWidth = 16;
                                var portsWidth = showPorts ? 12 : 0;
                                var cpuWidth = showCpu ? 8 : 0;
                                var memWidth = showMem ? 12 : 0;
                                var upWidth = 8;
                                var imageWidth = showImage ? Math.Max(10, body - 2 - nameWidth - stateWidth - portsWidth - cpuWidth - memWidth - upWidth) : 0;

                                string Compose((string Text, Color? Colour)[] cells, bool isSelected)
                                {
                                    var plain = string.Concat(cells.Select(x => x.Text));

                                    var markup = string.Concat(cells.Select(x => x.Colour is null
                                        ? Markup.Escape(x.Text)
                                        : $"[{x.Colour.Value.ToMarkup()}]{Markup.Escape(x.Text)}[/]"));

                                    if (plain.Length < body)
                                        markup += new string(' ', body - plain.Length);

                                    return isSelected ? $"[on #263041]{markup}[/]" : markup;
                                }

                                string Spread((string Text, Color? Colour)[] items)
                                {
                                    var gaps = Math.Max(1, items.Length - 1);
                                    var space = Math.Max(gaps, body - items.Sum(x => x.Text.Length));

                                    var markup = string.Empty;

                                    for (var i = 0; i < items.Length; i++)
                                    {
                                        var (text, colour) = items[i];

                                        markup += colour is null
                                            ? Markup.Escape(text)
                                            : $"[{colour.Value.ToMarkup()}]{Markup.Escape(text)}[/]";

                                        if (i < items.Length - 1)
                                            markup += new string(' ', space / gaps + (i < space % gaps ? 1 : 0));
                                    }

                                    return markup;
                                }

                                var bodyHeight = Math.Max(1, height - 9);
                                var first = selected / bodyHeight * bodyHeight;
                                var last = Math.Min(first + bodyHeight, rows.Count);

                                var page = new List<IRenderable>
                                {
                                    new Grid { Expand = true }
                                        .AddColumn(new GridColumn())
                                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                                        .AddRow("[bold]ThetaNexus[/]", $"[{Color.Grey.ToMarkup()}]{engine} · {containers.Count(x => x.State == "running")}/{containers.Count} running[/]"),
                                    new Rule { Style = new Style(Color.Grey35) },
                                    new Markup(Spread([.. sections.Select((x, i) => (i == section ? x.ToUpperInvariant() : x, i == section ? (Color?)Color.SteelBlue1 : Color.Grey35))])),
                                    new Rule { Style = new Style(Color.Grey35) },
                                    new Text(string.Empty)
                                };

                                var drawn = 0;

                                if (section != 0)
                                {
                                    page.Add(new Text(string.Empty));
                                    page.Add(new Markup($"[{Color.Grey.ToMarkup()}]{sections[section]} — not built yet[/]"));

                                    drawn = 1;
                                }
                                else
                                {
                                    var titles = new List<(string, Color?)> { ("  ", null) };

                                    for (var i = 0; i < columns.Length; i++)
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

                                        var label = i == sortBy ? $"{columns[i]} {(descending ? '▼' : '▲')}" : columns[i];

                                        titles.Add((i switch
                                        {
                                            4 or 5 => label.PadLeft(titleWidth - 1) + " ",
                                            6 => label.PadLeft(titleWidth),
                                            _ => label.PadRight(titleWidth)
                                        }, i == sortBy ? Color.SteelBlue1 : Color.Grey35));
                                    }

                                    page.Add(new Markup(Compose([.. titles], false)));

                                    if (rows.Count == 0)
                                    {
                                        page.Add(new Markup($"[{Color.Grey.ToMarkup()}]No containers on this engine.[/]"));

                                        drawn = 1;
                                    }

                                    for (var i = first; i < last; i++)
                                    {
                                        var (project, container) = rows[i];

                                        drawn++;

                                        if (container is null)
                                        {
                                            page.Add(new Markup(Compose(
                                            [
                                                (collapsed.Contains(project) ? "▶ " : "▼ ", Color.Grey),
                                                (project, Color.Khaki1),
                                                ($" ({groups.First(x => x.Key == project).Count()})", Color.Grey35)
                                            ], i == selected)));

                                            continue;
                                        }

                                        var status = container.Status ?? string.Empty;
                                        var paren = status.IndexOf('(');
                                        var exit = paren >= 0 && status.IndexOf(')') > paren ? status[paren..(status.IndexOf(')') + 1)] : string.Empty;

                                        var (glyph, colour) = container.State switch
                                        {
                                            "paused" => ("‖ paused", Color.SkyBlue1),
                                            "restarting" => ("◌ restarting", Color.Yellow),
                                            "created" => ("○ created", Color.Grey),
                                            "exited" => ($"✗ exited {exit}".TrimEnd(), exit == "(0)" ? Color.Grey : Color.Red3),
                                            "running" when status.Contains("(healthy)") => ("● healthy", Color.Green3_1),
                                            "running" when status.Contains("(unhealthy)") => ("● unhealthy", Color.Orange1),
                                            "running" when status.Contains("(health: starting)") => ("● starting", Color.Yellow),
                                            "running" => ("● running", Color.Green3_1),
                                            _ => (container.State ?? "?", Color.Grey)
                                        };

                                        var ports = (container.Ports ?? [])
                                            .Where(x => x.PublicPort > 0)
                                            .Select(x => $"{x.PublicPort}→{x.PrivatePort}")
                                            .Distinct()
                                            .ToList();

                                        var age = DateTime.UtcNow - container.Created;

                                        var cells = new List<(string, Color?)>
                                        {
                                            ("  ", null),
                                            (container.Names[0].TrimStart('/').PadRight(nameWidth), Color.SteelBlue1),
                                            (glyph.PadRight(stateWidth), colour)
                                        };

                                        if (showImage)
                                            cells.Add((container.Image.PadRight(imageWidth), Color.MediumPurple2));

                                        if (showPorts)
                                            cells.Add(((ports.Count > 0 ? string.Join(" ", ports) : "–").PadRight(portsWidth), Color.Aqua));

                                        if (showCpu)
                                            cells.Add(("–".PadLeft(cpuWidth - 1) + " ", Color.Grey35));

                                        if (showMem)
                                            cells.Add(("–".PadLeft(memWidth - 1) + " ", Color.Grey35));

                                        cells.Add(((age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s"
                                            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
                                            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
                                            : $"{(int)age.TotalDays}d").PadLeft(upWidth), Color.CadetBlue));

                                        page.Add(new Markup(Compose([.. cells], i == selected)));
                                    }
                                }

                                for (var i = drawn; i < bodyHeight; i++)
                                    page.Add(new Text(string.Empty));

                                page.Add(new Rule { Style = new Style(Color.Grey35) });
                                page.Add(new Markup(Spread(
                                [
                                    ("↑↓ move", Color.Grey),
                                    ("←→ section", Color.Grey),
                                    ("TAB sort", Color.Grey),
                                    ("c collapse", Color.Grey),
                                    ("⏎ details", Color.Grey35),
                                    ("␣ start/stop", Color.Grey35)
                                ])));

                                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                                ctx.Refresh();
                            }
                        });

                    cts.Cancel();

                    await events;

                    AnsiConsole.Clear();

                    return;
                }
                catch (Exception ex) when (ex is DockerApiException or TimeoutException or OperationCanceledException or HttpRequestException or IOException)
                {
                    cts.Cancel();

                    await events;

                    if (!await EngineDown.Show(ex))
                        return;
                }
            }
        }
    }
}
