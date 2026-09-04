using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using ThetaNexus.Shared;

namespace ThetaNexus
{
    internal static class ImageInfo
    {
        internal static async Task Display(DockerClient client, LiveDisplayContext ctx, ImagesListResponse image, CancellationToken token)
        {
            var inspect = await client.Images.InspectImageAsync(image.ID, token);
            var history = await client.Images.GetImageHistoryAsync(image.ID, token);

            var tagged = (image.RepoTags ?? []).FirstOrDefault() ?? "<none>:<none>";
            var colon = tagged.LastIndexOf(':');
            var hasTag = colon > tagged.LastIndexOf('/');

            var repository = hasTag ? tagged[..colon] : tagged;
            var tag = hasTag ? tagged[(colon + 1)..] : "<none>";

            var dirty = true;

            (string Text, Color Color)? notice = null;
            string? confirm = null;

            var offset = 0;
            var visible = 0;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    notice = null;

                    if (confirm != null)
                    {
                        confirm = null;

                        if (key.Key == ConsoleKey.Y)
                        {
                            try
                            {
                                await client.Images.DeleteImageAsync(image.ID, new ImageDeleteParameters(), token);
                                return;
                            }
                            catch (DockerApiException ex)
                            {
                                notice = (ex.Message, Color.Red3);
                            }
                        }

                        dirty = true;
                        continue;
                    }

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
                            offset = history.Count;
                            break;
                        case ConsoleKey.D:
                            confirm = image.Containers > 0
                                ? $"Remove {repository}:{tag}? It is used by {image.Containers} containers."
                                : $"Remove {repository}:{tag}? Nothing is using it.";
                            break;
                        case ConsoleKey.Y:
                            {
                                using var clip = Process.Start(new ProcessStartInfo("clip")
                                {
                                    RedirectStandardInput = true,
                                    UseShellExecute = false
                                });

                                if (clip is not null)
                                {
                                    await clip.StandardInput.WriteAsync(inspect.ID);

                                    clip.StandardInput.Close();

                                    await clip.WaitForExitAsync(token);
                                }

                                notice = ("id copied", Color.Green3_1);

                                break;
                            }
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

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn()
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.MediumPurple2}]{Markup.Escape(repository)}[/]   [{Color.Grey}]{Markup.Escape(tag)}[/] [{Color.Grey35}]·[/] [{Color.DarkOrange3}]{image.ID[(image.ID.IndexOf(':') + 1)..][..12]}[/]",
                            $"[{Color.Grey35}]used by {image.Containers} containers[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]IMAGE[/]",
                            $"[{Color.Grey35}]{UI.Size(inspect.Size)}[/] [{Color.Grey35}]·[/] [{Color.Grey35}]{inspect.RootFS.Layers.Count} layers[/]{(notice is { } n ? $" [{Color.Grey35}]·[/] [{n.Color}]{Markup.Escape(UI.Crop(n.Text, 60))}[/]" : string.Empty)}"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                int drawn = 0;

                Grid grid = new();

                grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                    .AddColumn(new GridColumn { NoWrap = true })
                    .AddRow(new Markup($"[{Color.Grey}]Created[/]"), new Markup($"[{Color.CadetBlue}]{inspect.Created.ToLocalTime():G}[/]"))
                    .AddRow(new Markup($"[{Color.Grey}]Platform[/]"), new Text($"{inspect.Os}/{inspect.Architecture}"))
                    .AddRow(new Markup($"[{Color.Grey}]Entrypoint[/]"), new Text(inspect.Config.Entrypoint is { Count: > 0 } entrypoint
                        ? $"{Markup.Escape(UI.Crop(string.Join(" ", entrypoint), body - 16))}"
                        : "-"))
                    .AddRow(new Markup($"[{Color.Grey}]Command[/]"), new Text(inspect.Config.Cmd is { Count: > 0 } cmd
                        ? $"{Markup.Escape(UI.Crop(string.Join(" ", cmd), body - 16))}"
                        : "-"))
                    .AddRow(new Markup($"[{Color.Grey}]Exposed[/]"), new Markup(inspect.Config.ExposedPorts is { Count: > 0 } exposed
                        ? string.Join("   ", exposed.Keys.Select(x => $"[{Color.Aqua}]{x}[/]"))
                        : $"[{Color.Grey35}]-[/]"))
                    .AddRow(new Markup($"[{Color.Grey}]Env[/]"), new Markup($"[{Color.Grey35}]{inspect.Config.Env?.Count ?? 0} variables[/]"))
                    .AddRow(new Markup($"[{Color.Grey}]Digest[/]"), new Markup($"[{Color.Grey35}]{UI.Crop(inspect.ID, body - 16)}[/]"));

                page.Add(grid);

                drawn += grid.Rows.Count;

                page.Add(new Text(string.Empty));
                page.Add(new Markup($"[bold {Color.SteelBlue1}]LAYERS[/]"));

                drawn += 2;

                var room = Math.Max(0, bodyHeight - drawn);

                visible = room;
                offset = Math.Clamp(offset, 0, Math.Max(0, history.Count - room));

                var more = offset + room < history.Count;
                var shown = more ? Math.Max(0, room - 1) : room;

                foreach (var layer in history.Skip(offset).Take(shown))
                {
                    page.Add(new Markup(UI.Compose(
                    [
                        ("  ", null),
                        (UI.Size(layer.Size).PadLeft(8) + "   ", layer.Size > 0 ? Color.Grey : Color.Grey35),
                        (UI.Crop(layer.CreatedBy ?? string.Empty, body - 13), layer.Size > 0 ? Color.Grey : Color.Grey35)
                    ], body, false)));

                    drawn++;
                }

                if (more)
                {
                    page.Add(new Markup(UI.Compose(
                    [
                        ("  ", null),
                        ($"… {history.Count - offset - shown} more layers below", Color.Grey35)
                    ], body, false)));

                    drawn++;
                }

                for (int i = drawn; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(confirm is { } question
                    ? UI.Spread([(question, Color.Grey), ("[y] yes   [n] no", Color.SteelBlue1)], body)
                    : UI.Spread(
                [
                    ("ESC back", Color.Grey),
                    ("↑↓ scroll", Color.Grey),
                    ("PgUp/PgDn page", Color.Grey),
                    ("home/end jump", Color.Grey),
                    ("y copy id", Color.Grey),
                    ("d remove", Color.Grey)
                ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}
