using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Diagnostics;
using ThetaNexus.Shared;
using Color = Spectre.Console.Color;

namespace ThetaNexus.Screens
{
    internal static class ImageDetails
    {
        internal static async Task<string[]?> Display(DockerClient client, LiveDisplayContext ctx, ImagesListResponse image, CancellationToken token)
        {
            var inspect = await client.Images.InspectImageAsync(image.ID, token);
            var history = await client.Images.GetImageHistoryAsync(image.ID, token);

            var tabs = Enum.GetNames<Models.ImageInfoTabs>()
                .Select(x => x.ToLower())
                .ToArray();

            var dirty = true;
            var reload = false;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;
            string? confirm = null;
            string? typed = null;

            var tab = 0;
            var offset = 0;
            var visible = 0;
            var hidden = true;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    notice = null;
                    noticed = DateTime.UtcNow;

                    if (typed != null)
                    {
                        if (key.Key == ConsoleKey.Escape)
                            typed = null;
                        else if (key.Key == ConsoleKey.Enter)
                        {
                            var wanted = typed.Trim();
                            var mark = wanted.LastIndexOf(':');

                            typed = null;

                            if (wanted.Length == 0)
                            {
                                dirty = true;
                                continue;
                            }

                            try
                            {
                                await client.Images.TagImageAsync(image.ID, new ImageTagParameters
                                {
                                    RepositoryName = mark > wanted.LastIndexOf('/')
                                        ? wanted[..mark]
                                        : wanted,
                                    Tag = mark > wanted.LastIndexOf('/')
                                        ? wanted[(mark + 1)..]
                                        : "latest"
                                }, token);

                                notice = ($"tagged {wanted}", Models.Outcome.Succeeded);
                                reload = true;
                            }
                            catch (DockerApiException ex)
                            {
                                notice = (UI.Reason(ex), Models.Outcome.Failed);
                            }
                        }
                        else if (key.Key == ConsoleKey.Backspace)
                            typed = typed.Length > 0
                                ? typed[..^1]
                                : typed;
                        else if (!char.IsControl(key.KeyChar))
                            typed += key.KeyChar;

                        dirty = true;
                        continue;
                    }

                    if (confirm != null)
                    {
                        var untag = confirm.StartsWith("Untag");

                        confirm = null;

                        if (key.Key == ConsoleKey.Y)
                        {
                            try
                            {
                                await client.Images.DeleteImageAsync(untag
                                    ? (inspect.RepoTags ?? []).FirstOrDefault() ?? image.ID
                                    : image.ID, new ImageDeleteParameters(), token);

                                if (!untag || (inspect.RepoTags?.Count ?? 0) <= 1)
                                    return null;

                                notice = ("tag deleted", Models.Outcome.Succeeded);
                                reload = true;
                            }
                            catch (DockerApiException ex)
                            {
                                notice = (UI.Reason(ex), Models.Outcome.Failed);
                            }
                        }

                        dirty = true;
                        continue;
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            return null;
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
                        case ConsoleKey.LeftArrow:
                            tab = (tab + tabs.Length - 1) % tabs.Length;
                            offset = 0;
                            break;
                        case ConsoleKey.RightArrow:
                            tab = (tab + 1) % tabs.Length;
                            offset = 0;
                            break;
                        case ConsoleKey.V when tab == (int)Models.ImageInfoTabs.Env:
                            hidden = !hidden;
                            break;
                        case ConsoleKey.Enter:
                            await RawJson.DisplayJson(ctx, $"[{Color.MediumPurple2}]{Markup.Escape((inspect.RepoTags ?? []).FirstOrDefault() ?? image.ID)}[/]", ["image", "inspect", image.ID], token);
                            break;
                        case ConsoleKey.R:
                            return ["run", "--rm", "-it", (inspect.RepoTags ?? []).FirstOrDefault() ?? image.ID, "sh", "-c", "command -v bash >/dev/null && exec bash || exec sh"];
                        case ConsoleKey.T:
                            typed = string.Empty;
                            break;
                        case ConsoleKey.U when inspect.RepoTags is { Count: > 0 }:
                            confirm = inspect.RepoTags.Count > 1
                                ? $"Untag {inspect.RepoTags[0]}? {inspect.RepoTags.Count - 1} tags remain."
                                : $"Untag {inspect.RepoTags[0]}? It is the last tag, the image goes with it.";
                            break;
                        case ConsoleKey.D:
                            confirm = image.Containers > 0
                                ? $"Delete this image? It is used by {image.Containers} containers."
                                : "Delete this image? Nothing is using it.";
                            break;
                        case ConsoleKey.Y:
                            {
                                using var clip = Process.Start(new ProcessStartInfo("clip")
                                {
                                    RedirectStandardInput = true,
                                    UseShellExecute = false
                                });

                                if (clip != null)
                                {
                                    await clip.StandardInput.WriteAsync(inspect.ID);

                                    clip.StandardInput.Close();

                                    await clip.WaitForExitAsync(token);
                                }

                                notice = ("id copied", Models.Outcome.Succeeded);

                                break;
                            }
                    }

                    dirty = true;
                    continue;
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

                if (reload)
                {
                    reload = false;

                    inspect = await client.Images.InspectImageAsync(image.ID, token);
                    history = await client.Images.GetImageHistoryAsync(image.ID, token);
                }

                var width = AnsiConsole.Profile.Width;
                var height = Console.WindowHeight;
                var body = width - 4;
                var bodyHeight = Math.Max(1, height - 9 - (notice == null ? 0 : 1));

                var tagged = (inspect.RepoTags ?? []).FirstOrDefault() ?? "<none>:<none>";
                var colon = tagged.LastIndexOf(':');
                var hasTag = colon > tagged.LastIndexOf('/');

                var repository = hasTag
                    ? tagged[..colon]
                    : tagged;
                var tag = hasTag
                    ? tagged[(colon + 1)..]
                    : "<none>";

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn()
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.MediumPurple2}]{Markup.Escape(repository)}[/]   [{Color.Grey}]{Markup.Escape(tag)}[/] [{Color.Grey35}]·[/] [{Color.DarkOrange3}]{image.ID[(image.ID.IndexOf(':') + 1)..][..12]}[/]",
                            $"[{Color.Grey35}]{UI.Size(inspect.Size)} · {inspect.RootFS.Layers.Count} layers · used by {image.Containers}[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Markup(UI.Spread([.. tabs.Select((x, i) => (i == tab ? x.ToUpperInvariant() : x, i == tab
                        ? (Color?)Color.SteelBlue1
                        : Color.Grey35))], body)),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                int drawn = 0;

                List<(string, Color?)[]> lines = new();

                Grid grid = new();

                switch ((Models.ImageInfoTabs)tab)
                {
                    case Models.ImageInfoTabs.Overview:
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true })
                                .AddRow(new Markup($"[{Color.Grey}]Created[/]"), new Markup($"[{Color.CadetBlue}]{inspect.Created.ToLocalTime():G}[/]"))
                                .AddRow(new Markup($"[{Color.Grey}]Platform[/]"), new Text($"{inspect.Os}/{inspect.Architecture}"))
                                .AddRow(new Markup($"[{Color.Grey}]Tags[/]"), new Markup(inspect.RepoTags is { Count: > 0 } all
                                    ? string.Join("   ", all.Select(x => $"[{Color.MediumPurple2}]{Markup.Escape(x)}[/]"))
                                    : $"[{Color.Grey35}]–[/]"))
                                .AddRow(new Markup($"[{Color.Grey}]Digests[/]"), new Markup(inspect.RepoDigests is { Count: > 0 } digests
                                    ? $"[{Color.Grey35}]{Markup.Escape(UI.Crop(string.Join("   ", digests), body - 16))}[/]"
                                    : $"[{Color.Grey35}]–[/]"))
                                .AddRow(new Markup($"[{Color.Grey}]Entrypoint[/]"), new Text(inspect.Config.Entrypoint is { Count: > 0 } entrypoint
                                    ? UI.Crop(string.Join(" ", entrypoint), body - 16)
                                    : "–"))
                                .AddRow(new Markup($"[{Color.Grey}]Command[/]"), new Text(inspect.Config.Cmd is { Count: > 0 } cmd
                                    ? UI.Crop(string.Join(" ", cmd), body - 16)
                                    : "–"))
                                .AddRow(new Markup($"[{Color.Grey}]WorkingDir[/]"), new Text(inspect.Config.WorkingDir is { Length: > 0 } dir
                                    ? dir
                                    : "–"))
                                .AddRow(new Markup($"[{Color.Grey}]User[/]"), new Text(inspect.Config.User is { Length: > 0 } user
                                    ? user
                                    : "root"))
                                .AddRow(new Markup($"[{Color.Grey}]Exposed[/]"), new Markup(inspect.Config.ExposedPorts is { Count: > 0 } exposed
                                    ? string.Join("   ", exposed.Keys.Select(x => $"[{Color.Aqua}]{x}[/]"))
                                    : $"[{Color.Grey35}]–[/]"))
                                .AddRow(new Markup($"[{Color.Grey}]Digest[/]"), new Markup($"[{Color.Grey35}]{UI.Crop(inspect.ID, body - 16)}[/]"));

                            break;
                        }
                    case Models.ImageInfoTabs.Layers:
                        {
                            lines = [..history.Select(x => new (string, Color?)[]
                            {
                                ("  ", null),
                                (UI.Size(x.Size).PadLeft(8) + "   ", x.Size > 0
                                    ? Color.Grey
                                    : Color.Grey35),
                                (UI.Crop(x.CreatedBy ?? string.Empty, body - 13), x.Size > 0
                                    ? Color.Grey
                                    : Color.Grey35)
                            })];

                            break;
                        }
                    case Models.ImageInfoTabs.Env:
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
                    case Models.ImageInfoTabs.Labels:
                        {
                            grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                                .AddColumn(new GridColumn { NoWrap = true });

                            if (inspect.Config.Labels is { Count: > 0 } labels)
                                labels
                                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                                    .ToList()
                                    .ForEach(x => grid.AddRow(new Markup($"[{Color.Grey}]{Markup.Escape(x.Key)}[/]"), new Markup($"[{Color.Grey}]{Markup.Escape(UI.Crop(x.Value, body - 40))}[/]")));
                            else
                                grid.AddRow(new Text(" "), new Markup($"[{Color.Grey}]This image has no labels.[/]"));

                            break;
                        }
                }

                if (grid.Columns.Count > 0)
                    page.Add(grid);

                drawn += grid.Rows.Count;

                var room = Math.Max(0, bodyHeight - drawn);

                visible = room;
                offset = Math.Clamp(offset, 0, Math.Max(0, lines.Count - room));

                var more = offset + room < lines.Count;
                var shown = more
                    ? Math.Max(0, room - 1)
                    : room;

                if (lines.Count == 0 && tab == (int)Models.ImageInfoTabs.Layers)
                {
                    page.Add(new Markup($"[{Color.Grey}]This image has no layer history.[/]"));

                    drawn++;
                }

                foreach (var line in lines.Skip(offset).Take(shown))
                {
                    page.Add(new Markup(UI.Compose(line, body, false)));

                    drawn++;
                }

                if (more)
                {
                    page.Add(new Markup(UI.Compose(
                    [
                        ("  ", null),
                        ($"… {lines.Count - offset - shown} more below", Color.Grey35)
                    ], body, false)));

                    drawn++;
                }

                for (int i = drawn; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(typed != null
                    ? UI.Spread([($"New tag (repository:tag): {typed}_", Color.Grey)], body)
                    : confirm is { } question
                        ? UI.Spread([(question, Color.Grey), ("[y] yes   [n] no", Color.SteelBlue1)], body)
                        : UI.Spread(tab == (int)Models.ImageInfoTabs.Env
                            ?
                            [
                                ("ESC back", Color.Grey),
                                ("←→ tab", Color.Grey),
                                ("⏎ raw JSON", Color.Grey),
                                ("v values", Color.Grey),
                                ("r run", Color.Grey),
                                ("d delete", Color.Grey)
                            ]
                            :
                            [
                                ("ESC back", Color.Grey),
                                ("←→ tab", Color.Grey),
                                ("↑↓ scroll", Color.Grey),
                                ("⏎ raw JSON", Color.Grey),
                                ("y copy id", Color.Grey),
                                ("t tag", Color.Grey),
                                ("u untag", Color.Grey),
                                ("r run", Color.Grey),
                                ("d delete", Color.Grey)
                            ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}
