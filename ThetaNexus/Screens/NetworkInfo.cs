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

namespace ThetaNexus.Screens
{
    internal static class NetworkInfo
    {
        internal static async Task Display(DockerClient client, LiveDisplayContext ctx, NetworkResponse network, CancellationToken token)
        {
            var inspect = await client.Networks.InspectNetworkAsync(network.ID, token);

            var builtin = network.Name is "bridge" or "host" or "none";

            var dirty = true;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;
            string? confirm = null;

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    notice = null;
                    noticed = DateTime.UtcNow;

                    if (confirm != null)
                    {
                        confirm = null;

                        if (key.Key == ConsoleKey.Y)
                        {
                            try
                            {
                                await client.Networks.DeleteNetworkAsync(network.ID, token);

                                return;
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
                        case ConsoleKey.Y:
                            {
                                using var clip = Process.Start(new ProcessStartInfo("clip")
                                {
                                    RedirectStandardInput = true,
                                    UseShellExecute = false
                                });

                                if (clip != null)
                                {
                                    await clip.StandardInput.WriteAsync(network.Name);

                                    clip.StandardInput.Close();

                                    await clip.WaitForExitAsync(token);
                                }

                                notice = ("name copied", Models.Outcome.Succeeded);

                                break;
                            }
                        case ConsoleKey.Enter:
                            await RawJson.DisplayJson(ctx,
                                $"[{Color.Aqua}]{Markup.Escape(network.Name)}[/]",
                                ["network", "inspect", network.ID], token);
                            break;
                        case ConsoleKey.D when builtin:
                            notice = ("built-in networks cannot be deleted", Models.Outcome.Failed);
                            break;
                        case ConsoleKey.D:
                            confirm = inspect.Containers is { Count: > 0 }
                                ? $"Delete {network.Name}? {inspect.Containers.Count} containers are still connected."
                                : $"Delete {network.Name}? Nothing is connected.";
                            break;
                        case ConsoleKey.Escape:
                            return;
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

                var pool = (inspect.IPAM?.Config ?? []).FirstOrDefault(x => !string.IsNullOrEmpty(x.Subnet));

                var age = DateTime.UtcNow - inspect.Created;

                var flags = string.Join(" · ", new (string Name, bool On)[]
                {
                    ("internal", inspect.Internal),
                    ("attachable", inspect.Attachable),
                    ("ipv6", inspect.EnableIPv6),
                    ("ingress", inspect.Ingress)
                }.Where(x => x.On).Select(x => x.Name));

                var connected = (inspect.Containers?.Values ?? [])
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.Aqua}]{Markup.Escape(network.Name)}[/]   [{Color.Grey}]{Markup.Escape(inspect.Driver ?? "–")}[/] [{Color.Grey35}]·[/] [{Color.Grey}]{Markup.Escape(inspect.Scope ?? "–")}[/]",
                            $"[{Color.Grey35}]{connected.Count} containers connected[/]"
                        ),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]NETWORK[/]",
                            $"[{Color.Grey35}]{Markup.Escape(pool?.Subnet ?? "–")}[/] [{Color.Grey35}]·[/] [{Color.Grey35}]created {(age.TotalHours < 1
                                ? $"{(int)age.TotalMinutes}m"
                                : age.TotalDays < 1
                                    ? $"{(int)age.TotalHours}h"
                                    : $"{(int)age.TotalDays}d")} ago[/]"
                        ),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                Grid grid = new();

                grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                    .AddColumn(new GridColumn { NoWrap = true })
                    .AddRow(new Markup($"[{Color.Grey}]Driver[/]"), new Text(inspect.Driver ?? "–"))
                    .AddRow(new Markup($"[{Color.Grey}]Scope[/]"), new Text(inspect.Scope ?? "–"))
                    .AddRow(new Markup($"[{Color.Grey}]Created[/]"), new Markup($"[{Color.CadetBlue}]{inspect.Created.ToLocalTime():G}[/]"))
                    .AddRow(new Markup($"[{Color.Grey}]IPAM[/]"), new Text(inspect.IPAM?.Driver ?? "–"))
                    .AddRow(new Markup($"[{Color.Grey}]Subnet[/]"), new Text(pool?.Subnet ?? "–"))
                    .AddRow(new Markup($"[{Color.Grey}]Gateway[/]"), new Text(pool?.Gateway is { Length: > 0 } gateway
                        ? gateway
                        : "–"))
                    .AddRow(new Markup($"[{Color.Grey}]Flags[/]"), new Text(flags.Length == 0
                        ? "–"
                        : flags))
                    .AddRow(new Markup($"[{Color.Grey}]Project[/]"), new Markup(inspect.Labels != null && inspect.Labels.TryGetValue("com.docker.compose.project", out var project)
                        ? $"[{Color.Khaki1}]{Markup.Escape(project)}[/]"
                        : "–"));

                page.Add(grid);

                page.Add(new Text(string.Empty));
                page.Add(new Markup($"[bold {Color.SteelBlue1}]CONNECTED CONTAINERS[/]"));

                var drawn = 2 + grid.Rows.Count;

                if (connected.Count == 0)
                {
                    page.Add(new Markup($"[{Color.Grey}]No containers are connected.[/]"));

                    drawn++;
                }

                foreach (var endpoint in connected.Take(Math.Max(0, bodyHeight - drawn)))
                {
                    page.Add(new Markup(UI.Compose(
                        [
                        ("  ", null),
                        (UI.Crop(endpoint.Name ?? "–", 21).PadRight(22), Color.SteelBlue1),
                        (UI.Crop(endpoint.IPv4Address is { Length: > 0 } ipv4
                            ? ipv4
                            : "–", 19).PadRight(20), Color.CadetBlue),
                        (UI.Crop(endpoint.MacAddress is { Length: > 0 } mac
                            ? mac
                            : "–", Math.Max(1, body - 45)), Color.Grey35)
                        ], body, false)));

                    drawn++;
                }

                for (int i = drawn; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(confirm is { } question
                    ? UI.Spread([(question, Color.Grey), ("[y] yes   [n] no", Color.SteelBlue1)], body)
                    : UI.Spread(
                        [
                            ("ESC back", Color.Grey),
                            ("⏎ raw JSON", Color.Grey),
                            ("y copy name", Color.Grey),
                            ("d delete", Color.Grey)
                        ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}
