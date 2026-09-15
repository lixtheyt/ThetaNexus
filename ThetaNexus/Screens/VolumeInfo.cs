using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using System.Diagnostics;
using Color = Spectre.Console.Color;
using System.Globalization;
using Spectre.Console.Rendering;
using ThetaNexus.Shared;

namespace ThetaNexus.Screens
{
    internal static class VolumeInfo
    {
        internal static async Task<string[]?> Display(DockerClient client, LiveDisplayContext ctx, VolumeResponse volume, CancellationToken token)
        {
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, token);

            List<(string, MountPoint)> mounts = new();

            using var df = Process.Start(new ProcessStartInfo("docker")
            {
                ArgumentList = { "system", "df", "-v", "--format", "{{range.Volumes}}{{if eq .Name \"" + volume.Name + "\"}}{{.Size}}{{end}}{{end}}" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            });

            var size = df == null
                ? "–"
                : (await df.StandardOutput.ReadToEndAsync(token)).Trim();

            if (df != null)
                await df.WaitForExitAsync(token);

            if (size.Length == 0)
                size = "–";

            var dirty = true;

            (string Text, Models.Outcome Outcome)? notice = null;
            var noticed = DateTime.UtcNow;
            string? typed = null;

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
                            if (typed == volume.Name)
                            {
                                if (mounts.Count > 0)
                                {
                                    notice = ("volume is mounted by containers, cannot remove", Models.Outcome.Failed);

                                    typed = null;
                                    dirty = true;

                                    continue;
                                }

                                try
                                {
                                    await client.Volumes.RemoveAsync(volume.Name, false, token);

                                    return null;
                                }
                                catch (DockerApiException ex)
                                {
                                    notice = (UI.Reason(ex), Models.Outcome.Failed);
                                }
                            }
                            else
                            {
                                if (typed.Length > 0)
                                    notice = ("invalid name", Models.Outcome.Failed);

                                typed = null;
                                dirty = true;

                                continue;
                            }

                            typed = null;
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
                                    await clip.StandardInput.WriteAsync(volume.Name);

                                    clip.StandardInput.Close();

                                    await clip.WaitForExitAsync(token);
                                }

                                notice = ("name copied", Models.Outcome.Succeeded);

                                break;
                            }
                        case ConsoleKey.Enter:
                            await RawJson.DisplayJson(ctx,
                                $"[{Color.Yellow}]{Markup.Escape(volume.Name)}[/]",
                                ["volume", "inspect", volume.Name], token);
                            break;
                        case ConsoleKey.D:
                            typed = string.Empty;
                            break;
                        case ConsoleKey.B:
                            return ["run", "--rm", "-it", "-v", volume.Name + ":/v", "-w", "/v", "alpine", "sh"];
                        case ConsoleKey.Escape:
                            return null;
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

                var drawn = 0;

                var age = DateTime.TryParse(volume.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created)
                    ? DateTime.UtcNow - created.ToUniversalTime()
                    : TimeSpan.Zero;

                var page = new List<IRenderable>
                {
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[{Color.Yellow}]{Markup.Escape(volume.Name)}[/]   [{Color.Grey}]{Markup.Escape(volume.Driver)}[/] [{Color.Grey35}]·[/] [{Color.Grey}]{Markup.Escape(volume.Scope)}[/]",
                            $"[{Color.Grey35}]mounted by {containers.Count(x => (x.Mounts ?? []).Any(m => m.Name == volume.Name))} containers[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Grid { Expand = true }
                        .AddColumn(new GridColumn())
                        .AddColumn(new GridColumn { Alignment = Justify.Right })
                        .AddRow(
                            $"[bold {Color.SteelBlue1}]VOLUME[/]",
                            $"[{Color.Grey35}]{size}[/] [{Color.Grey35}]·[/] [{Color.Grey35}]created {(age == TimeSpan.Zero ? "–" : age.TotalDays < 1 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d")} ago[/]"),
                    new Rule { Style = new Style(Color.Grey35) },
                    new Text(string.Empty)
                };

                Grid grid = new();

                grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 4, 0), NoWrap = true })
                    .AddColumn(new GridColumn { NoWrap = true })
                    .AddRow(new Markup($"[{Color.Grey}]Driver[/]"), new Text(volume.Driver))
                    .AddRow(new Markup($"[{Color.Grey}]Scope[/]"), new Text(volume.Scope))
                    .AddRow(new Markup($"[{Color.Grey}]Mountpoint[/]"), new Text(UI.Crop(volume.Mountpoint, body - 16)))
                    .AddRow(new Markup($"[{Color.Grey}]Created[/]"), new Markup(age == TimeSpan.Zero
                        ? "–"
                        : $"[{Color.CadetBlue}]{created.ToLocalTime():G}[/]"))
                    .AddRow(new Markup($"[{Color.Grey}]Project[/]"), new Markup(volume.Labels != null && volume.Labels.TryGetValue("com.docker.compose.project", out var project)
                        ? $"[{Color.Khaki1}]{Markup.Escape(project)}[/]"
                        : "–"));

                page.Add(grid);

                page.Add(new Text(string.Empty));
                page.Add(new Markup($"[bold {Color.SteelBlue1}]MOUNTED BY[/]"));

                drawn += 2;

                mounts = containers
                    .SelectMany(x => (x.Mounts ?? []).Select(m => (Container: x.Names[0].TrimStart('/'), Mount: m)))
                    .Where(x => x.Mount.Name == volume.Name)
                    .OrderBy(x => x.Container, StringComparer.Ordinal)
                    .ToList();

                if (mounts.Count == 0)
                {
                    page.Add(new Markup($"[{Color.Grey}]Not mounted by any container.[/]"));

                    drawn++;
                }

                foreach (var (container, mount) in mounts.Take(Math.Max(0, bodyHeight - drawn)))
                {
                    page.Add(new Markup(UI.Compose(
                        [
                            ("  ", null),
                            (UI.Crop(container, 21).PadRight(22), Color.SteelBlue1),
                            (UI.Crop(mount.Destination, body - 27).PadRight(body - 26), Color.Grey),
                            (mount.RW ? "rw" : "ro", Color.Grey35)
                        ], body, false)));

                    drawn++;
                }

                drawn += grid.Rows.Count;

                for (int i = drawn; i < bodyHeight; i++)
                    page.Add(new Text(string.Empty));

                if (notice is { } toast)
                    page.Add(new Markup(UI.Toast(toast, body)));

                page.Add(new Rule { Style = new Style(Color.Grey35) });
                page.Add(new Markup(typed != null
                    ? UI.Spread([($"Type the volume name to remove it permanently: {typed}_", Color.Grey)], body)
                    : UI.Spread(
                    [
                        ("ESC back", Color.Grey),
                        ("⏎ raw JSON", Color.Grey),
                        ("y copy name", Color.Grey),
                        ("b browse", Color.Grey),
                        ("d remove", Color.Grey)
                    ], body)));

                ctx.UpdateTarget(new Padder(new Rows(page), new Padding(2, 1, 2, 0)));
                ctx.Refresh();
            }
        }
    }
}
