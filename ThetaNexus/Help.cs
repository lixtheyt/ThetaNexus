using Spectre.Console;
using Spectre.Console.Rendering;
using System.Net;
using System.Reflection;
using System.Text.Json;
using ThetaNexus.Shared;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class Help
    {
        private const string Releases = "https://github.com/lixtheyt/ThetaNexus/releases";
        private const string Api = "https://api.github.com/repos/lixtheyt/ThetaNexus/releases/latest";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

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
            ("main list", "?", "this screen"),
            ("main list", "q", "quit"),

            ("container", "␣", "start or stop"),
            ("container", "r", "restart"),
            ("container", "p", "pause or unpause"),
            ("container", "k", "kill, no SIGTERM and no waiting"),
            ("container", "x", "remove"),
            ("container", "l", "logs"),
            ("container", "s", "stats"),
            ("container", "e", "open a shell inside it"),
            ("container", "o", "open its published port in a browser"),
            ("container", "⏎", "raw JSON"),
            ("container", "v", "reveal env values, on the env tab"),
            ("container", "?", "this screen"),

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

            ("help", "u", "check github for a newer version"),

            ("anywhere", "esc", "go back")
        ];

        internal static async Task Display(LiveDisplayContext ctx, CancellationToken token)
        {
            var offset = 0;
            var visible = 0;
            var dirty = true;
            var frame = 0;

            Task<string?>? checking = null;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    notice = null;

                    switch (key.Key)
                    {
                        case ConsoleKey.U when checking == null:
                            checking = Latest(token);
                            break;
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

                if (checking != null)
                {
                    if (checking.IsCompleted)
                    {
                        try
                        {
                            notice = Compare(await checking);
                        }
                        catch (Exception failure)
                        {
                            notice = (failure.Message, Models.Outcome.Failed);
                        }

                        noticed = DateTime.UtcNow;
                        checking = null;
                        dirty = true;
                    }
                    else
                    {
                        var turn = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 80 % 10);

                        if (turn != frame)
                        {
                            frame = turn;
                            dirty = true;
                        }
                    }
                }

                if (notice != null && DateTime.UtcNow - noticed > TimeSpan.FromSeconds(8))
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

                if (checking != null)
                    page.Add(new Markup(UI.Compose(
                    [
                        ("  ", null),
                        ($"{"\u280b\u2819\u2839\u2838\u283c\u2834\u2826\u2827\u2807\u280f"[frame]}  ", Color.SteelBlue1),
                        ("asking github for the latest release", Color.Grey)
                    ], body, false)));
                else if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(UI.Spread(
                [
                    ("ESC back", Color.Grey),
                    ("↑↓ scroll", Color.Grey),
                    ("PgUp/PgDn page", Color.Grey),
                    ("u updates", checking == null ? Color.Grey : Color.SteelBlue1),
                    ("? close", Color.Grey)
                ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }

        private static async Task<string?> Latest(CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api);

            request.Headers.UserAgent.ParseAdd("ThetaNexus");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, token);
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException("could not reach github, check the connection");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"github answered {(int)response.StatusCode} {response.ReasonPhrase}");

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));

                return json.RootElement.TryGetProperty("tag_name", out var tag)
                    ? tag.GetString()
                    : null;
            }
        }
        
        private static (string, Models.Outcome) Compare(string? tag)
        {
            var running = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

            if (string.IsNullOrWhiteSpace(tag))
                return ($"you have {running}, and github has no published release to compare it with", Models.Outcome.Warned);

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(running, out var current))
                return ($"you have {running}, the latest release is {tag}", Models.Outcome.Warned);

            return latest > current
                ? ($"{tag} is out, you have {running}, get it at {Releases}", Models.Outcome.Warned)
                : ($"{running} is the latest version", Models.Outcome.Succeeded);
        }
    }
}
