using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using ThetaNexus.Shared;

namespace ThetaNexus.Screens
{
    internal static class RunContainer
    {
        internal static async Task<string?> Display(DockerClient client, LiveDisplayContext ctx, ImagesListResponse image, IList<ContainerListResponse> existing, CancellationToken token)
        {
            var tagged = (image.RepoTags ?? []).FirstOrDefault() ?? image.ID;

            string[] labels = ["Name", "Command", "Ports", "Volumes", "Env", "Network", "Restart", "Remove on exit"];
            string[] fields = ["", "", "", "", "", "bridge", "no", "no"];

            string[] hints =
            [
                "leave empty and the engine names it",
                "leave empty for the image default",
                "host:container, comma separated",
                "volume:/path, comma separated",
                "KEY=value, comma separated",
                "bridge, host, none or a named network",
                "⏎ toggles",
                "⏎ toggles"
            ];

            var field = 0;
            string? editing = null;
            var dirty = true;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    notice = null;
                    noticed = DateTime.UtcNow;

                    if (editing != null)
                    {
                        if (key.Key == ConsoleKey.Escape)
                            editing = null;
                        else if (key.Key == ConsoleKey.Enter)
                        {
                            fields[field] = editing.Trim();
                            editing = null;
                        }
                        else if (key.Key == ConsoleKey.Backspace)
                            editing = editing.Length > 0
                                ? editing[..^1]
                                : editing;
                        else if (!char.IsControl(key.KeyChar))
                            editing += key.KeyChar;

                        dirty = true;
                        continue;
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            return null;
                        case ConsoleKey.UpArrow:
                            field = Math.Max(0, field - 1);
                            break;
                        case ConsoleKey.DownArrow:
                            field = Math.Min(labels.Length - 1, field + 1);
                            break;
                        case ConsoleKey.Enter when field >= 6:
                            fields[field] = field == 6
                                ? fields[6] == "no"
                                    ? "unless-stopped"
                                    : "no"
                                : fields[7] == "no"
                                    ? "yes"
                                    : "no";
                            break;
                        case ConsoleKey.Enter:
                            editing = fields[field];
                            break;
                        case ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                            {
                                if (fields[0].Length > 0 && existing.Any(x => x.Names.Any(n => n.TrimStart('/') == fields[0])))
                                {
                                    notice = ($"a container named {fields[0]} already exists", Models.Outcome.Failed);
                                    dirty = true;
                                    continue;
                                }

                                Dictionary<string, IList<PortBinding>> bindings = new();

                                foreach (var pair in fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var parts = pair.Trim().Split(':');

                                    if (parts.Length != 2 || !ushort.TryParse(parts[0], out var host) || !ushort.TryParse(parts[1], out _) || host == 0)
                                    {
                                        notice = ($"{pair.Trim()} is not a port mapping, write it as host:container", Models.Outcome.Failed);
                                        break;
                                    }

                                    var holder = existing.FirstOrDefault(x => (x.Ports ?? []).Any(p => p.PublicPort == host));

                                    if (holder != null)
                                    {
                                        notice = ($"port {host} is held by {holder.Names[0].TrimStart('/')}", Models.Outcome.Failed);
                                        break;
                                    }

                                    bindings[$"{parts[1]}/tcp"] = [new PortBinding { HostPort = parts[0] }];
                                }

                                if (notice != null)
                                {
                                    dirty = true;
                                    continue;
                                }

                                try
                                {
                                    var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
                                    {
                                        Image = tagged,
                                        Name = fields[0].Length > 0
                                            ? fields[0]
                                            : null,
                                        Cmd = fields[1].Length > 0
                                            ? fields[1].Split(' ')
                                            : null,
                                        Tty = true,
                                        OpenStdin = true,
                                        Env = fields[4].Length > 0
                                            ? fields[4].Split(',')
                                            : null,
                                        ExposedPorts = bindings.Count > 0
                                            ? bindings.ToDictionary(x => x.Key, _ => new EmptyStruct())
                                            : null,
                                        HostConfig = new HostConfig
                                        {
                                            PortBindings = bindings.Count > 0
                                                ? bindings
                                                : null,
                                            Binds = fields[3].Length > 0
                                                ? fields[3].Split(',')
                                                : null,
                                            NetworkMode = fields[5],
                                            RestartPolicy = new RestartPolicy
                                            {
                                                Name = fields[6] == "no"
                                                    ? RestartPolicyKind.No
                                                    : RestartPolicyKind.UnlessStopped
                                            },
                                            AutoRemove = fields[7] == "yes"
                                        }
                                    }, token);

                                    try
                                    {
                                        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), token);
                                    }
                                    catch (DockerApiException)
                                    {
                                        await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true }, token);
                                        throw;
                                    }

                                    return fields[0].Length > 0
                                        ? fields[0]
                                        : created.ID[..12];
                                }
                                catch (DockerApiException failure)
                                {
                                    notice = (UI.Reason(failure), Models.Outcome.Failed);
                                }

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

                var width = AnsiConsole.Profile.Width;
                var height = Console.WindowHeight;
                var body = width - 4;
                var bodyHeight = Math.Max(1, height - 9 - (notice == null ? 0 : 1));

                var page = new List<IRenderable> 
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.MediumPurple2}]{Markup.Escape(tagged)}[/]",
                            $"[{Color.Grey35}]{existing.Count} containers on this engine[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]RUN[/]",
                            string.Empty),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                for (int i = 0; i < labels.Length; i++)
                {
                    var shown = i == field && editing != null
                        ? editing + "_"
                        : fields[i];

                    page.Add(new Markup(UI.Compose(
                    [
                        (i == field ? "  ▸ " : "    ", Color.SteelBlue1),
                        (labels[i].PadRight(16), Color.Grey),
                        (UI.Crop(shown.Length > 0 ? shown : "-", 30).PadRight(32), shown.Length > 0 ? Color.White : Color.Grey35),
                        (UI.Crop(hints[i], Math.Max(8, body - 54)), Color.Grey35)
                    ], body, i == field)));
                }

                for (int i = labels.Length; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(UI.Spread(
                [
                    ("ESC cancel", Color.Grey),
                    ("↑↓ field", Color.Grey),
                    ("⏎ edit", Color.Grey),
                    ("^R run", Color.SteelBlue1)
                ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}
