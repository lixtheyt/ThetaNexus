using Docker.DotNet;
using Docker.DotNet.Models;

using Spectre.Console;
using Spectre.Console.Rendering;
using ThetaNexus.Shared;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class Details
    {
        internal static async Task Display(DockerClient client, LiveDisplayContext ctx, ContainerListResponse container)
        {
            var tabs = new[] { "overview", "env", "mounts", "networks", "health" };

            var tab = 0;
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

                        case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                            tab = (tab + tabs.Length - 1) % tabs.Length;
                            break;

                        case ConsoleKey.LeftArrow:
                            tab = (tab + tabs.Length - 1) % tabs.Length;
                            break;

                        case ConsoleKey.Tab:
                            tab = (tab + 1) % tabs.Length;
                            break;

                        case ConsoleKey.RightArrow:
                            tab = (tab + 1) % tabs.Length;
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

                var (glyph, colour) = UI.Glyph(container);
                var name = container.Names[0].TrimStart('/');
                var id = container.ID[..12];

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.SteelBlue1.ToMarkup()}]{Markup.Escape(name)}[/]   [{Color.MediumPurple2.ToMarkup()}]{Markup.Escape(container.Image)}[/] [{Color.Grey35.ToMarkup()}]·[/] [{Color.DarkOrange3.ToMarkup()}]{id}[/]",
                            $"[{colour.ToMarkup()}]{Markup.Escape(glyph.Replace("  ", " "))}[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Markup(Spread([.. tabs.Select((x, i) => (i == tab ? x.ToUpperInvariant() : x, i == tab ? (Color?)Color.SteelBlue1 : Color.Grey35))])),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                var bodyHeight = Math.Max(1, height - 9);

                page.Add(new Markup($"[{Color.Grey.ToMarkup()}]{tabs[tab]} — not built yet[/]"));

                for (var i = 1; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(Spread(
                [
                    ("ESC back", Color.Grey),
                    ("TAB tab", Color.Grey),
                    ("⏎ raw JSON", Color.Grey35),
                    ("l logs", Color.Grey35),
                    ("s stats", Color.Grey35),
                    ("e shell", Color.Grey35),
                    ("␣ start/stop", Color.Grey35)
                ])));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}
