using System.Globalization;

using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using Color = Spectre.Console.Color;
using ThetaNexus.Shared;

namespace ThetaNexus
{
    internal static class MainList
    {
        internal static async Task Display(DockerClient client)
        {
            var sections = Enum.GetNames<Models.MainListSections>()
                .Select(x => x.ToLower())
                .ToArray();
            var sorts = Enum.GetNames<Models.MainListSorts>()
                .Select(x => x.ToUpper())
                .ToArray();

            var collapsed = new HashSet<string>();
            var selected = 0;
            var section = 0;
            var sortBy = 0;
            var descending = false;
            
            while (true)
            {
                using var cts = new CancellationTokenSource();

                var events = Task.CompletedTask;
                var stats = Task.CompletedTask;

                try
                {
                    await client.System.PingAsync(cts.Token);

                    var version = await client.System.GetVersionAsync(cts.Token);

                    var engine = $"engine {version.Version}";

                    var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cts.Token);
                    stats = Task.Run(()
                        => ContainerStats.Watch(client, cts.Token));

                    var refreshed = DateTime.UtcNow;
                    var stale = false;
                    var dirty = true;

                    events = Task.Run(async () =>
                    {
                        try
                        {
                            await client.System.MonitorEventsAsync(new ContainerEventsParameters(), new Progress<Message>(_ => stale = true), cts.Token);
                        }
                        catch (Exception)
                        {
                            stale = true;
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
                                            sortBy = (sortBy + 1) % sorts.Length;
                                            break;

                                        case ConsoleKey.C when rows.Count > 0:
                                            var toToggle = rows[selected].Project;

                                            if (collapsed.Remove(toToggle))
                                                break;

                                            collapsed.Add(toToggle);
                                            selected = rows.FindIndex(x => x.Project == toToggle && x.Container is null);
                                            break;

                                        case ConsoleKey.Spacebar when rows.Count > 0 && rows[selected].Container is { } target:
                                            await ContainerActions.StartStop(client, target, cts.Token);
                                            break;

                                        case ConsoleKey.Q:
                                            return;

                                        case ConsoleKey.Enter when rows.Count > 0 && rows[selected].Container is { } open:
                                            await Details.Display(client, ctx, open, cts.Token);
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
                                var portsWidth = showPorts ? 15 : 0;
                                var cpuWidth = showCpu ? 8 : 0;
                                var memWidth = showMem ? 12 : 0;
                                var upWidth = 8;
                                var imageWidth = showImage ? Math.Max(10, body - 2 - nameWidth - stateWidth - portsWidth - cpuWidth - memWidth - upWidth) : 0;

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
                                    new Markup(UI.Spread([.. sections.Select((x, i) => (i == section ? x.ToUpperInvariant() : x, i == section ? (Color?)Color.SteelBlue1 : Color.Grey35))], body)),
                                    new Rule { Style = new Style(Color.Grey35) },
                                    new Text(string.Empty)
                                };

                                ContainerStats.Track(containers
                                    .Where(x => x.State == "running")
                                    .Select(x => x.ID));

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

                                    for (int i = 0; i < sorts.Length; i++)
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

                                        var label = i == sortBy ? $"{sorts[i]} {(descending ? '▼' : '▲')}" : sorts[i];

                                        titles.Add((i switch
                                        {
                                            4 or 5 => label.PadLeft(titleWidth - 1) + " ",
                                            6 => label.PadLeft(titleWidth),
                                            _ => label.PadRight(titleWidth)
                                        }, i == sortBy ? Color.SteelBlue1 : Color.Grey35));
                                    }

                                    page.Add(new Markup(UI.Compose([.. titles], body, false)));

                                    if (rows.Count == 0)
                                    {
                                        page.Add(new Markup($"[{Color.Grey.ToMarkup()}]No containers on this engine.[/]"));

                                        drawn = 1;
                                    }

                                    for (int i = first; i < last; i++)
                                    {
                                        var (project, container) = rows[i];

                                        drawn++;

                                        if (container is null)
                                        {
                                            page.Add(new Markup(UI.Compose(
                                            [
                                                (collapsed.Contains(project) ? "▶ " : "▼ ", Color.Grey),
                                                (project, Color.Khaki1),
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
                                            (container.Names[0].TrimStart('/').PadRight(nameWidth), Color.SteelBlue1),
                                            (glyph.PadRight(stateWidth), color)
                                        };

                                        if (showImage)
                                            cells.Add((container.Image.PadRight(imageWidth), Color.MediumPurple2));

                                        if (showPorts)
                                            cells.Add((UI.Crop(ports.Count switch
                                            {
                                                0 => "–",
                                                1 => ports[0],
                                                _ => $"{ports[0]} +{ports.Count - 1}"
                                            }, portsWidth - 1).PadRight(portsWidth), Color.Aqua));

                                        if (showCpu)
                                            cells.Add((ContainerStats.Stats(container.ID)?.Cpu is { } cpu 
                                                ? cpu.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(cpuWidth - 2) + "% "
                                                : "–".PadLeft(cpuWidth - 1) + " ", Color.Grey35));

                                        if (showMem)
                                            cells.Add((ContainerStats.Stats(container.ID) is { } used
                                                ? $"{used.Memory / 1024 / 1024}/{used.Limit / 1024 / 1024} MB".PadLeft(memWidth - 1) + " "
                                                : "–".PadLeft(memWidth - 1) + " ", Color.Grey35));

                                        cells.Add(((age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s"
                                            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
                                            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
                                            : $"{(int)age.TotalDays}d").PadLeft(upWidth), Color.CadetBlue));

                                        page.Add(new Markup(UI.Compose([.. cells], body, i == selected)));
                                    }
                                }

                                for (int i = drawn; i < bodyHeight; i++)
                                    page.Add(new Text(string.Empty));

                                page.Add(new Rule { Style = new Style(Color.Grey35) });
                                page.Add(new Markup(UI.Spread(
                                [
                                    ("↑↓ move", Color.Grey),
                                    ("←→ section", Color.Grey),
                                    ("TAB sort", Color.Grey),
                                    ("SHIFT+TAB invert sort", Color.Grey),
                                    ("c collapse", Color.Grey),
                                    ("⏎ details", Color.Grey),
                                    ("␣ start/stop", Color.Grey),
                                    ("q quit", Color.Grey)
                                ], body)));

                                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                                ctx.Refresh();
                            }
                        });

                    cts.Cancel();

                    await events;

                    await stats;

                    AnsiConsole.Clear();

                    return;
                }
                catch (Exception ex) when (ex is DockerApiException or TimeoutException or OperationCanceledException or HttpRequestException or IOException)
                {
                    cts.Cancel();

                    await events;

                    await stats;

                    if (!await EngineDown.Display(ex))
                        return;
                }
            }
        }
    }
}
