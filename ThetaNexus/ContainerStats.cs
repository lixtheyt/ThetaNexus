using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;
using ThetaNexus.Shared;

namespace ThetaNexus
{
    internal static class ContainerStats
    {
        internal static async Task Display(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container, CancellationToken token)
        {
            ContainerStatsResponse? latest = null;
            var dirty = true;

            var tabs = Enum.GetNames<Models.StatsTabs>()
                .Select(x => x.ToLower())
                .ToArray();

            var tab = 0;
            var graphs = false;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;

            _ = client.Containers.GetContainerStatsAsync(container.ID,
                new ContainerStatsParameters { Stream = true },
                new Progress<ContainerStatsResponse>(x =>
                {
                    latest = x;
                    dirty = true;
                }),
                token);

            List<double> cpuHistory = new();
            List<double> memHistory = new();
            List<double> netHistory = new();
            List<double> diskHistory = new();

            var lastDisk = (Read: 0L, Write: 0L);
            var diskRate = (Read: 0.0, Write: 0.0);

            ulong[] lastCores = [];
            double[] coreShare = [];

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

                    notice = null;
                    noticed = DateTime.UtcNow;

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            return;
                        case ConsoleKey.C:
                            cpuHistory.Clear();
                            memHistory.Clear();
                            netHistory.Clear();
                            diskHistory.Clear();
                            break;
                        case ConsoleKey.Spacebar:
                            await ContainerActions.StartStop(client, container, inspect.State.Status, token);
                            break;
                        case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift) && !graphs:
                        case ConsoleKey.LeftArrow when !graphs:
                            tab = (tab + tabs.Length - 1) % tabs.Length;
                            break;
                        case ConsoleKey.Tab when !graphs:
                        case ConsoleKey.RightArrow when !graphs:
                            tab = (tab + 1) % tabs.Length;
                            break;
                        case ConsoleKey.G:
                            graphs = !graphs;
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

                var stats = latest;

                var cache = stats?.MemoryStats.Stats != null && stats.MemoryStats.Stats.TryGetValue("inactive_file", out var inactive) 
                    ? inactive 
                    : 0;

                if (stats != null && stats.Read != seen)
                {
                    seen = stats.Read;

                    var cpuDelta = (double)stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
                    var systemDelta = (double)stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
                    var cpus = stats.CPUStats.OnlineCPUs > 0 ? stats.CPUStats.OnlineCPUs : 1;

                    var seconds = (stats.Read - stats.PreRead).TotalSeconds;
                    var rx = stats.Networks?.Values.Sum(x => (long)x.RxBytes) ?? 0;
                    var tx = stats.Networks?.Values.Sum(x => (long)x.TxBytes) ?? 0;

                    var blkio = stats.BlkioStats?.IoServiceBytesRecursive ?? [];

                    var read = blkio
                        .Where(x => x.Op?.StartsWith("r", StringComparison.OrdinalIgnoreCase) == true)
                        .Sum(x => (long)x.Value);

                    var write = blkio
                        .Where(x => x.Op?.StartsWith("w", StringComparison.OrdinalIgnoreCase) == true)
                        .Sum(x => (long)x.Value);

                    rate = seconds > 0 && last.Rx > 0
                        ? ((rx - last.Rx) / seconds, (tx - last.Tx) / seconds)
                        : (null, null);

                    last = (rx, tx);

                    diskRate = seconds > 0 && lastDisk.Read > 0
                        ? ((read - lastDisk.Read) / seconds, (write - lastDisk.Write) / seconds)
                        : (0, 0);

                    lastDisk = (read, write);

                    var perCpu = stats.CPUStats.CPUUsage.PercpuUsage ?? [];

                    coreShare = lastCores.Length == perCpu.Count
                        ? [.. perCpu.Select((x, i) => (double)(x - lastCores[i]))]
                        : new double[perCpu.Count];

                    lastCores = [.. perCpu];

                    cpuHistory.Add(systemDelta > 0 && cpuDelta > 0
                        ? cpuDelta / systemDelta * cpus * 100.0
                        : 0);

                    memHistory.Add((stats.MemoryStats.Usage - cache) / 1024.0 / 1024.0);

                    netHistory.Add((rate.Down ?? 0) + (rate.Up ?? 0));

                    diskHistory.Add(diskRate.Read + diskRate.Write);

                    foreach (var series in new[] { cpuHistory, memHistory, netHistory, diskHistory })
                        if (series.Count > 400)
                            series.RemoveRange(0, series.Count - 400);
                }

                var used = stats == null ? 0 : (long)(stats.MemoryStats.Usage - cache);
                var limit = stats == null ? 0 : (long)stats.MemoryStats.Limit;

                var (glyph, color) = ContainerActions.Pending(container.ID) is { } verb
                    ? ($"◌  {verb}", Color.Yellow)
                    : UI.Glyph(inspect);

                string Rate(double? bytes) => bytes == null ? "–" : UI.Size((long)bytes) + "/s";

                var bodyHeight = Math.Max(1, height - 9 - (notice == null ? 0 : 1));

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.SteelBlue1}]{Markup.Escape(container.Names[0].TrimStart('/'))}[/]   [{Color.MediumPurple2}]{Markup.Escape(container.Image)}[/] [{Color.Grey35}]·[/] [{Color.DarkOrange3}]{container.ID[..12]}[/]",
                            $"[{color}]{Markup.Escape(glyph.Replace("  ", " "))}[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Markup(UI.Spread([..tabs.Select((x, i) => (i == tab && !graphs
                        ? x.ToUpperInvariant()
                        : x,
                        i == tab && !graphs
                            ? (Color?)Color.SteelBlue1
                            : graphs
                                ? Color.Grey35
                                : Color.Grey))], body)),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var drawn = 0;

                var facts = body >= 88 ? 34 : 0;
                var gutter = facts == 0 ? 0 : body >= 110 ? 8 : 4;
                var panel = facts == 0 ? body : body - facts - gutter;
                var chart = panel - 7;
                var tall = Math.Clamp(bodyHeight - 9, 4, 20);
                var wide = Math.Clamp(panel - 22, 10, 32);

                List<(string Text, Color? Color)[]> right = new();

                var upFor = DateTime.TryParse(inspect.State.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                    ? DateTime.UtcNow - started.ToUniversalTime()
                    : TimeSpan.Zero;

                (string Text, Color? Color)[][] process =
                [
                    [("PROCESS", Color.SteelBlue1)],
                    [("pids".PadRight(14), Color.Grey), ($"{stats?.PidsStats?.Current ?? 0} / {(stats?.PidsStats?.Limit is > 0 and < ulong.MaxValue ? stats.PidsStats.Limit.ToString() : "∞")}", Color.Grey)],
                    [("restarts".PadRight(14), Color.Grey), ($"{inspect.RestartCount}", Color.Grey)],
                    [("uptime".PadRight(14), Color.Grey), (upFor == TimeSpan.Zero ? "–" : upFor.TotalHours < 1 ? $"{(int)upFor.TotalMinutes}m" : upFor.TotalDays < 1 ? $"{(int)upFor.TotalHours}h {upFor.Minutes}m" : $"{(int)upFor.TotalDays}d", Color.CadetBlue)]
                ];

                if (!graphs)
                {
                    switch ((Models.StatsTabs)tab)
                    {
                        case Models.StatsTabs.Cpu:
                            {
                                var cpuLimit = inspect.HostConfig.NanoCPUs > 0
                                    ? inspect.HostConfig.NanoCPUs / 1_000_000_000.0 * 100.0
                                    : 100.0;

                                var now = cpuHistory.Count > 0 ? cpuHistory[^1] : 0;
                                var peak = cpuHistory.Count > 0 ? cpuHistory.Max() : 0;

                                var throttling = stats?.CPUStats.ThrottlingData;
                                var periods = throttling?.Periods ?? 0;
                                var throttled = throttling?.ThrottledPeriods ?? 0;

                                right =
                                [
                                    [("THROTTLING", Color.SteelBlue1)],
                                    [("periods".PadRight(14), Color.Grey), ($"{periods}", Color.Grey)],
                                    [("throttled".PadRight(14), Color.Grey), ($"{throttled}   {(periods > 0 ? throttled * 100.0 / periods : 0):0.0}%", throttled > 0 ? Color.Orange1 : Color.Grey)],
                                    [("time".PadRight(14), Color.Grey), ($"{(throttling?.ThrottledTime ?? 0) / 1_000_000_000.0:0.0} s", throttled > 0 ? Color.Orange1 : Color.Grey)],
                                    [("limit".PadRight(14), Color.Grey), (inspect.HostConfig.NanoCPUs > 0 ? $"{cpuLimit:0.0}%   {inspect.HostConfig.NanoCPUs / 1_000_000_000.0:0.##} cpus" : "none", Color.Grey)],
                                    [],
                                    [("CPU", Color.SteelBlue1)],
                                    [("online cores".PadRight(14), Color.Grey), ($"{stats?.CPUStats.OnlineCPUs ?? 0}", Color.Grey)],
                                    [("average 60".PadRight(14), Color.Grey), ($"{(cpuHistory.Count > 0 ? cpuHistory.TakeLast(60).Average() : 0):0.0}%", Color.Grey)],
                                    [("samples".PadRight(14), Color.Grey), ($"{cpuHistory.Count}", Color.Grey)],
                                    [],
                                    .. process
                                ];
                                
                                Chart("CPU", $"{now:0.0}%", $"peak {peak:0.0}%", cpuHistory, cpuLimit, x => $"{x:0}%",
                                    [Color.Red3, Color.Orange1, Color.Yellow, Color.Green3_1]);

                                Row([]);
                                Row([("PER CORE", Color.SteelBlue1)]);

                                var busiest = coreShare.Length > 0 ? coreShare.Sum() : 0;

                                if (coreShare.Length == 0)
                                    Row([("  the engine does not report per-core usage", Color.Grey35)]);

                                for (int i = 0; i < Math.Min(coreShare.Length, 6); i++)
                                {
                                    var share = busiest > 0 ? coreShare[i] / busiest : 0;
                                    var fill = (int)Math.Round(wide * share);

                                    Row([
                                        ($" cpu{i}".PadRight(7), Color.Grey),
                                        (new string('█', fill), Color.Green3_1),
                                        (new string('░', wide - fill), Color.Grey35),
                                        ($"  {now * share:0.0}%", Color.Grey)
                                    ]);
                                }

                                Stack();

                                break;
                            }
                        case Models.StatsTabs.Memory:
                            {
                                var ceiling = inspect.HostConfig.Memory > 0 ? inspect.HostConfig.Memory : (long)(stats?.MemoryStats.Limit ?? 0);
                                var scale = ceiling > 0 ? ceiling / 1024.0 / 1024.0 : 64.0;

                                var rss = stats?.MemoryStats.Stats != null && stats.MemoryStats.Stats.TryGetValue("rss", out var r) ? (long)r : used;
                                var swap = stats?.MemoryStats.Stats != null && stats.MemoryStats.Stats.TryGetValue("swap", out var s) ? (long)s : 0;
                                var minor = stats?.MemoryStats.Stats != null && stats.MemoryStats.Stats.TryGetValue("pgfault", out var f) ? f : 0;
                                var major = stats?.MemoryStats.Stats != null && stats.MemoryStats.Stats.TryGetValue("pgmajfault", out var m) ? m : 0;

                                right =
                                [
                                    [("MEMORY", Color.SteelBlue1)],
                                    [("peak".PadRight(14), Color.Grey), (UI.Size((long)(stats?.MemoryStats.MaxUsage ?? 0)), Color.Grey)],
                                    [("limit".PadRight(14), Color.Grey), (ceiling > 0 ? UI.Size(ceiling) : "none", Color.Grey)],
                                    [("headroom".PadRight(14), Color.Grey), (ceiling > 0 ? UI.Size(ceiling - used) : "–", Color.Grey)],
                                    [("failures".PadRight(14), Color.Grey), ($"{stats?.MemoryStats.Failcnt ?? 0}", (stats?.MemoryStats.Failcnt ?? 0) > 0 ? Color.Orange1 : Color.Grey)],
                                    [],
                                    [("PAGE FAULTS", Color.SteelBlue1)],
                                    [("minor".PadRight(14), Color.Grey), ($"{minor}", Color.Grey)],
                                    [("major".PadRight(14), Color.Grey), ($"{major}", major > 0 ? Color.Orange1 : Color.Grey)],
                                    [],
                                    .. process
                                ];

                                Chart("MEM", $"{used / 1024.0 / 1024.0:0.0} / {scale:0} MB", $"{(scale > 0 ? used / 1024.0 / 1024.0 / scale * 100 : 0):0.0}%", memHistory, scale, x => $"{x:0}", [Color.CadetBlue]);
                                
                                Row([]);
                                Row([("BREAKDOWN", Color.SteelBlue1)]);

                                var total = ceiling > 0 ? (double)ceiling : 1;

                                Bar("rss", rss / total, UI.Size(rss), Color.CadetBlue);
                                Bar("cache", cache / total, UI.Size((long)cache), Color.Aqua);
                                Bar("swap", swap / total, UI.Size(swap), Color.MediumPurple2);
                                Bar("free", (total - used) / total, UI.Size((long)(total - used)), Color.Grey35);

                                Stack();

                                break;
                            }
                        case Models.StatsTabs.Network:
                            {
                                var scale = netHistory.Count > 0 ? Math.Max(1.0, netHistory.Max()) : 1.0;

                                var rxPackets = stats?.Networks?.Values.Sum(x => (long)x.RxPackets) ?? 0;
                                var txPackets = stats?.Networks?.Values.Sum(x => (long)x.TxPackets) ?? 0;
                                var dropped = stats?.Networks?.Values.Sum(x => (long)x.RxDropped + (long)x.TxDropped) ?? 0;
                                var errors = stats?.Networks?.Values.Sum(x => (long)x.RxErrors + (long)x.TxErrors) ?? 0;

                                right =
                                [
                                    [("TRAFFIC", Color.SteelBlue1)],
                                    [("received".PadRight(14), Color.Grey), (UI.Size(last.Rx), Color.Grey)],
                                    [("sent".PadRight(14), Color.Grey), (UI.Size(last.Tx), Color.Grey)],
                                    [("interfaces".PadRight(14), Color.Grey), ($"{stats?.Networks?.Count ?? 0}", Color.Grey)],
                                    [],
                                    [("PACKETS", Color.SteelBlue1)],
                                    [("in".PadRight(14), Color.Grey), ($"{rxPackets}", Color.Grey)],
                                    [("out".PadRight(14), Color.Grey), ($"{txPackets}", Color.Grey)],
                                    [("dropped".PadRight(14), Color.Grey), ($"{dropped}", dropped > 0 ? Color.Orange1 : Color.Grey)],
                                    [("errors".PadRight(14), Color.Grey), ($"{errors}", errors > 0 ? Color.Red3 : Color.Grey)],
                                    [],
                                    .. process
                                ];

                                Chart("NET", Rate((rate.Down ?? 0) + (rate.Up ?? 0)), $"peak {Rate(scale)}", netHistory, scale, x => UI.Size((long)x), [Color.Aqua]);

                                Row([]);
                                Row([("BREAKDOWN", Color.SteelBlue1)]);

                                Bar("down", (rate.Down ?? 0) / scale, Rate(rate.Down), Color.Aqua);
                                Bar("up", (rate.Up ?? 0) / scale, Rate(rate.Up), Color.Aqua);

                                Stack();

                                break;
                            }
                        case Models.StatsTabs.Disk:
                        {
                            var scale = diskHistory.Count > 0 ? Math.Max(1.0, diskHistory.Max()) : 1.0;

                            right =
                            [
                                [("BLOCK IO", Color.SteelBlue1)],
                                [("read".PadRight(14), Color.Grey), (UI.Size((long)diskRate.Read) + "/s", Color.Grey)],
                                [("write".PadRight(14), Color.Grey), (UI.Size((long)diskRate.Write) + "/s", Color.Grey)],
                                [("peak".PadRight(14), Color.Grey), (Rate(scale), Color.Grey)],
                                [],
                                .. process
                            ];

                            Chart("DISK", Rate(diskRate.Read + diskRate.Write), $"peak {Rate(scale)}", diskHistory, scale, x => UI.Size((long)x), [Color.MediumPurple2]);

                            Row([]);
                            Row([("BREAKDOWN", Color.SteelBlue1)]);

                            Bar("read", diskRate.Read / scale, Rate(diskRate.Read), Color.MediumPurple2);
                            Bar("write", diskRate.Write / scale, Rate(diskRate.Write), Color.MediumPurple2);

                            Stack();

                            break;
                        }
                    }
                }
                else
                {
                    var cpuLimit = inspect.HostConfig.NanoCPUs > 0
                        ? inspect.HostConfig.NanoCPUs / 1_000_000_000.0 * 100.0
                        : 100.0;

                    var ceiling = inspect.HostConfig.Memory > 0 ? inspect.HostConfig.Memory : (long)(stats?.MemoryStats.Limit ?? 0);
                    var memScale = ceiling > 0 ? ceiling / 1024.0 / 1024.0 : 64.0;

                    var netScale = netHistory.Count > 0 ? Math.Max(1.0, netHistory.Max()) : 1.0;
                    var diskScale = diskHistory.Count > 0 ? Math.Max(1.0, diskHistory.Max()) : 1.0;

                    var throttling = stats?.CPUStats.ThrottlingData;
                    var dot = "   ·   ";

                    var unit = Math.Max(1, (bodyHeight - 8) / 6);
                    var big = Math.Max(1, bodyHeight - 8 - 4 * unit);

                    Block("CPU",
                        $"{(cpuHistory.Count > 0 ? cpuHistory[^1] : 0):0.0}%",
                        $"peak {(cpuHistory.Count > 0 ? cpuHistory.Max() : 0):0.0}%{dot}throttled {throttling?.ThrottledPeriods ?? 0} of {throttling?.Periods ?? 0}{dot}limit {(inspect.HostConfig.NanoCPUs > 0 ? $"{inspect.HostConfig.NanoCPUs / 1_000_000_000.0:0.##} cpus" : "none")}",
                        cpuHistory, cpuLimit, $"{cpuLimit:0}%", big,
                        [Color.Red3, Color.Orange1, Color.Yellow, Color.Green3_1]);

                    Block("MEM",
                        $"{used / 1024.0 / 1024.0:0.0} / {memScale:0} MB",
                        $"{(memScale > 0 ? used / 1024.0 / 1024.0 / memScale * 100 : 0):0.0}%{dot}peak {UI.Size((long)(stats?.MemoryStats.MaxUsage ?? 0))}{dot}cache {UI.Size((long)cache)}",
                        memHistory, memScale, $"{memScale:0} MB", 2 * unit,
                        [Color.CadetBlue]);

                    Block("NET",
                        Rate((rate.Down ?? 0) + (rate.Up ?? 0)),
                        $"total ↓ {UI.Size(last.Rx)}  ↑ {UI.Size(last.Tx)}{dot}pkts {stats?.Networks?.Values.Sum(x => (long)x.RxPackets) ?? 0} / {stats?.Networks?.Values.Sum(x => (long)x.TxPackets) ?? 0}{dot}drop {stats?.Networks?.Values.Sum(x => (long)x.RxDropped + (long)x.TxDropped) ?? 0}",
                        netHistory, netScale, UI.Size((long)netScale), unit,
                        [Color.Aqua]);

                    Block("DISK",
                        Rate(diskRate.Read + diskRate.Write),
                        $"read {UI.Size((long)diskRate.Read)}/s{dot}write {UI.Size((long)diskRate.Write)}/s{dot}pids {stats?.PidsStats?.Current ?? 0}",
                        diskHistory, diskScale, UI.Size((long)diskScale), unit,
                        [Color.MediumPurple2]);
                }

                for (int i = drawn; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(UI.Spread(
                    graphs
                        ?
                        [
                            ("ESC back", Color.Grey),
                            ("g tabs", Color.Grey),
                            ("c clear", Color.Grey),
                            ("␣ start/stop", Color.Grey)
                        ]
                        :
                        [
                            ("ESC back", Color.Grey),
                            ("TAB/←→ tab", Color.Grey),
                            ("g graphs", Color.Grey),
                            ("c clear", Color.Grey),
                            ("␣ start/stop", Color.Grey)
                        ]
                    , body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
                continue;

                void Row((string Text, Color?)[] left)
                {
                    var cells = new List<(string, Color?)>(left);
                    var used = left.Sum(x => x.Text.Length);

                    if (facts > 0)
                    {
                        if (used < panel + gutter)
                            cells.Add((new string(' ', panel + gutter - used), null));

                        if (drawn < right.Count)
                            cells.AddRange(right[drawn]);
                    }

                    page.Add(new Markup(UI.Compose([.. cells], body, false)));

                    drawn++;
                }

                void Stack()
                {
                    if (facts > 0)
                        return;

                    Row([]);

                    foreach (var line in right)
                        Row([(" ", null), .. line]);
                }

                void Bar(string name, double share, string value, Color colour)
                {
                    var fill = (int)Math.Round(wide * Math.Clamp(share, 0, 1));

                    Row([
                        ($" {name}".PadRight(8), Color.Grey),
                        (new string('█', fill), colour),
                        (new string('░', wide - fill), Color.Grey35),
                        ($"  {value}", Color.Grey)
                    ]);
                }

                void Chart(string label, string value, string note, IReadOnlyList<double> series, double scale, Func<double, string> mark, Color[] palette)
                {

                    Row([(label, Color.Grey), ($"   {value}".PadRight(20), Color.White), (UI.Crop(note, Math.Max(8, panel - 24)), Color.Grey35)]);

                    var rows = UI.Graph(series, chart, tall, scale);
                    var every = Math.Max(1, tall / 4);

                    for (int i = 0; i < rows.Length; i++)
                        Row([
                            ((i % every == 0 ? UI.Crop(mark(scale * (tall - i) / tall), 6) : string.Empty).PadLeft(6) + " ", Color.Grey35),
                            (rows[i], palette[Math.Min(palette.Length - 1, i * palette.Length / rows.Length)])
                        ]);

                    Row([("     0 ", Color.Grey35), (new string('─', chart), Color.Grey35)]);
                }

                void Block(string label, string value, string note, IReadOnlyList<double> series, double scale, string top, int tall, Color[] colours)
                {
                    var span = body - 10;
                    var room = Math.Max(8, body - label.Length - value.Length - 4);
                    var trimmed = UI.Crop(note, room);

                    Row([
                        (label, Color.Grey),
                        ($"   {value}", Color.White),
                        (new string(' ', Math.Max(1, body - label.Length - value.Length - 3 - trimmed.Length)), null),
                        (trimmed, Color.Grey35)
                    ]);

                    var rows = UI.Graph(series, span, tall, scale);

                    for (int i = 0; i < rows.Length; i++)
                        Row([
                            ((i == 0 ? UI.Crop(top, 8) : string.Empty).PadLeft(8) + "  ", Color.Grey35),
                            (rows[i], colours[Math.Min(colours.Length - 1, i * colours.Length / rows.Length)])
                        ]);

                    Row([("       0  ", Color.Grey35), (new string('─', span), Color.Grey35)]);
                }
            }
        }

    }
}