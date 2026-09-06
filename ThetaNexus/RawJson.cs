using Spectre.Console;
using Spectre.Console.Rendering;
using System.Diagnostics;
using ThetaNexus.Shared;

namespace ThetaNexus
{
    internal static class RawJson
    {
        internal static async Task DisplayJson(LiveDisplayContext ctx, string heading, string[] arguments, CancellationToken token)
        {
            var info = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var child = Process.Start(info);

            var json = child == null
                ? string.Empty
                : await child.StandardOutput.ReadToEndAsync(token);

            if (child != null)
                await child.WaitForExitAsync(token);

            string[] lines = string.IsNullOrWhiteSpace(json)
                ? ["docker returned nothing. The object is most likely gone."]
                : [..json.Split('\n').Select(x => x.TrimEnd('\r'))];

            var captured = DateTime.Now;

            var offset = 0;
            var visible = 0;
            var dirty = true;

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
                            offset--;
                            break;
                        case ConsoleKey.DownArrow:
                            offset++;
                            break;
                        case ConsoleKey.PageUp:
                            offset -= Math.Max(1, visible);
                            break;
                        case ConsoleKey.PageDown:
                            offset += Math.Max(1, visible);
                            break;
                        case ConsoleKey.Home:
                            offset = 0;
                            break;
                        case ConsoleKey.End:
                            offset = lines.Length;
                            break;
                    }

                    dirty = true;
                    continue;
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
                var bodyHeight = Math.Max(1, height - 9);

                visible = bodyHeight;
                offset = Math.Clamp(offset, 0, Math.Max(0, lines.Length - bodyHeight));

                var last = Math.Min(lines.Length, offset + bodyHeight);

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            heading,
                            string.Empty),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]RAW JSON[/]",
                            $"[{Color.Grey35}]captured {captured:HH:mm:ss}[/] [{Color.Grey35}]·[/] [{Color.CadetBlue}]{offset + 1}–{last}[/] [{Color.Grey35}]of {lines.Length} lines[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var window = lines.Skip(offset).Take(bodyHeight).ToArray();

                foreach (var line in window)
                    page.Add(new Text(UI.Crop(line, body), new Style(Color.Grey)));

                for (int i = window.Length; i < bodyHeight; i++)
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

    }
}