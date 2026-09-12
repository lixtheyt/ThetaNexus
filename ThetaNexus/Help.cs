using Spectre.Console;
using Spectre.Console.Rendering;
using ThetaNexus.Shared;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class Help
    {
        private static readonly (string Screen, string Shown, string Label)[] _keys =
        [
            ("main list", "↑↓", "move the cursor"),
            ("main list", "←→", "switch section"),
            ("main list", "1–5", "jump straight to a section"),
            ("main list", "TAB", "sort by the next column"),
            ("main list", "SHIFT+TAB", "invert the sort"),
            ("main list", "c", "collapse a compose project"),
            ("main list", "x", "remove the selected container"),
            ("main list", "/", "filter by name"),
            ("main list", "⏎", "open details"),
            ("main list", ".", "action menu for the selected row"),
            ("main list", "Del", "prune unused things in this section, always asks first"),
            ("main list", "q", "quit"),

            ("container", "␣", "start or stop"),
            ("container", "r", "restart"),
            ("container", "p", "pause or unpause"),
            ("container", "k", "kill, no SIGTERM and no waiting"),
            ("container", "x", "remove"),
            ("container", "l", "logs"),
            ("container", "s", "stats"),
            ("container", "e", "open a shell inside it"),
            ("container", "⏎", "raw JSON"),
            ("container", "v", "reveal env values, on the env tab"),

            ("image", "⏎", "details"),
            ("image", "n", "run a new container from it"),
            ("image", "t", "tag"),
            ("image", "u", "untag"),
            ("image", "r", "run it in a shell"),
            ("image", "y", "copy the id"),
            ("image", "d", "delete"),

            ("volume", "⏎", "details"),
            ("volume", "b", "browse it in a shell"),
            ("volume", "y", "copy the name"),
            ("volume", "d", "remove, type the name to confirm"),

            ("network", "⏎", "details"),
            ("network", "y", "copy the name"),
            ("network", "d", "delete, refused for bridge, host and none"),

            ("events", "f", "filter by type"),
            ("events", "␣", "freeze the view without losing events"),
            ("events", "c", "clear the buffer"),
            ("events", "SHIFT+TAB", "newest or oldest first"),

            ("stats", "TAB/←→", "switch tab"),
            ("stats", "g", "all four graphs at once"),
            ("stats", "c", "clear the history"),

            ("logs", "f", "follow, End also resumes it"),
            ("logs", "↑↓", "scroll, scrolling up stops following"),
            ("logs", "PgUp PgDn", "page"),
            ("logs", "/", "search"),
            ("logs", "n N", "next or previous match"),
            ("logs", "w", "wrap long lines instead of cropping"),
            ("logs", "t", "timestamps from the engine"),
            ("logs", "+ -", "double or halve how many lines are kept"),
            ("logs", "c", "clear what is on screen"),
            ("logs", "SHIFT+S", "save the buffer to a file"),

            ("files", "⏎", "enter a directory"),
            ("files", "Backspace", "go up one level"),

            ("anywhere", "?", "this screen"),
            ("anywhere", "esc", "go back")
        ];

        internal static async Task Display(LiveDisplayContext ctx, CancellationToken token)
        {
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
                        case ConsoleKey.Q:
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
                            offset = int.MaxValue;
                            break;
                        case var _ when key.KeyChar == '?':
                            return;
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

                List<(string Text, Color? Color)[]> lines = [];

                foreach (var group in _keys.GroupBy(x => x.Screen))
                {
                    lines.Add([(group.Key.ToUpperInvariant(), Color.SteelBlue1)]);

                    foreach (var (_, shown, label) in group)
                        lines.Add([("  ", null), (shown.PadRight(14), Color.Khaki1), (label, Color.Grey)]);

                    lines.Add([]);
                }

                visible = bodyHeight;
                offset = Math.Clamp(offset, 0, Math.Max(0, lines.Count - bodyHeight));

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            "[bold]ThetaNexus[/]",
                            $"[{Color.Grey}]every key the app knows[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]HELP[/]",
                            $"[{Color.Grey35}]{lines.Count} lines[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var window = lines.Skip(offset).Take(bodyHeight).ToArray();

                foreach (var line in window)
                    page.Add(new Markup(UI.Compose(line, body, false)));

                for (int i = window.Length; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(UI.Spread(
                [
                    ("ESC back", Color.Grey),
                    ("↑↓ scroll", Color.Grey),
                    ("PgUp/PgDn page", Color.Grey),
                    ("? close", Color.Grey)
                ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}