using Docker.DotNet.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;
using System.Text.Json;
using Docker.DotNet;
using Color = Spectre.Console.Color;

namespace ThetaNexus.Shared
{
    internal static class UI
    {
        private static readonly byte[] _left = [0x01, 0x02, 0x04, 0x40];
        private static readonly byte[] _right = [0x08, 0x10, 0x20, 0x80];

        internal static (string Text, Color Color) Glyph(ContainerListResponse container)
        {
            var status = container.Status ?? string.Empty;
            var paren = status.IndexOf('(');
            var exit = paren >= 0 && status.IndexOf(')') > paren ? status[paren..(status.IndexOf(')') + 1)] : string.Empty;

            return container.State switch
            {
                "paused" => ("▎▎ paused", Color.SkyBlue1),
                "restarting" => ("◌  restarting", Color.Yellow),
                "created" => ("○  created", Color.Grey),
                "exited" => ($"✗  exited {exit}".TrimEnd(), exit == "(0)" ? Color.Grey : Color.Red3),
                "running" when status.Contains("(healthy)") => ("●  healthy", Color.Green3_1),
                "running" when status.Contains("(unhealthy)") => ("●  unhealthy", Color.Orange1),
                "running" when status.Contains("(health: starting)") => ("●  starting", Color.Yellow),
                "running" => ("●  running", Color.Green3_1),
                _ => (container.State ?? "?", Color.Grey)
            };
        }

        internal static (string Text, Color Color) Glyph(ContainerInspectResponse inspect)
        {
            var state = inspect.State;

            return state switch
            {
                { Paused: true } => ("▎▎ paused", Color.SkyBlue1),
                { Restarting: true } => ("◌  restarting", Color.Yellow),
                { Running: true, Health.Status: "unhealthy" } => ("●  unhealthy", Color.Orange1),
                { Running: true, Health.Status: "starting" } => ("●  starting", Color.Yellow),
                { Running: true, Health.Status: "healthy" } => ("●  healthy", Color.Green3_1),
                { Running: true } => ("●  running", Color.Green3_1),
                { Status: "created" } => ("○  created", Color.Grey),
                _ => ($"✗  exited ({state.ExitCode})", state.ExitCode == 0 ? Color.Grey : Color.Red3)
            };
        }

        internal static string Compose((string Text, Color? Color)[] cells, int body, bool isSelected)
        {
            var plain = string.Concat(cells.Select(x => x.Text));

            var markup = string.Concat(cells.Select(x => x.Color == null
                ? Markup.Escape(x.Text)
                : $"[{x.Color.Value.ToMarkup()}]{Markup.Escape(x.Text)}[/]"));

            if (plain.Length < body)
                markup += new string(' ', body - plain.Length);

            return isSelected ? $"[on #263041]{markup}[/]" : markup;
        }

        internal static string Spread((string Text, Color? Color)[] items, int body)
        {
            var gaps = Math.Max(1, items.Length - 1);
            var space = Math.Max(gaps, body - items.Sum(x => x.Text.Length));

            var markup = string.Empty;

            for (int i = 0; i < items.Length; i++)
            {
                var (text, color) = items[i];

                markup += color == null
                    ? Markup.Escape(text)
                    : $"[{color.Value.ToMarkup()}]{Markup.Escape(text)}[/]";

                if (i < items.Length - 1)
                    markup += new string(' ', space / gaps + (i < space % gaps ? 1 : 0));
            }

            return markup;
        }

        internal static string Size(long bytes)
            => bytes >= 1_000_000_000 ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : bytes >= 1_000_000 ? (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                : bytes >= 1_000 ? (bytes / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " kB"
                : bytes + " B";

        internal static string Toast((string Text, Models.Outcome Outcome) notice, int body)
        {
            var (band, glyph, colour) = notice.Outcome switch
            {
                Models.Outcome.Failed => ("#3a1014", "✗  ", Color.Red1),
                Models.Outcome.Warned => ("#3a2a10", "!  ", Color.Orange1),
                _ => ("#12301a", "●  ", Color.Green3_1)
            };

            return $"[on {band}]{Compose(
            [
                ("  ", null),
                (glyph, colour),
                (Crop(notice.Text, body - 5), colour)
            ], body, false)}[/]";
        }

        internal static IRenderable Menu((string Text, Color? Color)[] heading, (string Key, string Label)[] items, int selected, int body)
        {
            var inner = Math.Clamp(items.Max(x => x.Key.Length + x.Label.Length) + 12, 34, Math.Max(34, body - 8));

            List<IRenderable> rows =
            [
                new Markup(Spread(heading, inner)),
                new Markup($"[{Color.Grey35}]{new string('─', inner)}[/]")
            ];

            for (int i = 0; i < items.Length; i++)
                rows.Add(new Markup(Compose(
                [
                    (" ", null),
                    (items[i].Key.PadRight(5), Color.Khaki1),
                    (items[i].Label, i == selected
                        ? Color.White
                        : Color.Grey)
                ], inner, i == selected)));

            rows.Add(new Text(string.Empty));
            rows.Add(new Markup(Compose(
            [
                (" ", null),
                ("⏎ run     ↑↓ move     esc cancel", Color.Grey35)
            ], inner, false)));

            return new Padder(
                new Panel(new Rows(rows))
                {
                    Border = BoxBorder.Square,
                    BorderStyle = new Style(Color.Grey35),
                    Padding = new Padding(2, 1, 2, 1)
                },
                new Padding(Math.Max(0, (body - inner - 6) / 2), 0, Math.Max(0, body - inner - 6 - Math.Max(0, (body - inner - 6) / 2)), 0));
        }

        internal static string Reason(Exception failure)
        {
            if (failure is not DockerApiException api || string.IsNullOrWhiteSpace(api.ResponseBody))
                return failure.Message;

            try
            {
                return JsonDocument.Parse(api.ResponseBody).RootElement.TryGetProperty("message", out var message)
                    ? message.GetString() ?? failure.Message
                    : failure.Message;
            }
            catch (JsonException)
            {
                return api.ResponseBody.Trim();
            }
        }

        internal static string[] Graph(IReadOnlyList<double> samples, int width, int height, double scale)
        {
            var dots = height * 4;
            var need = width * 2;

            var filled = new int[need];

            for (int i = 0; i < need; i++)
            {
                var index = samples.Count - need + i;

                filled[i] = index < 0
                    ? 0
                    : Math.Clamp((int)Math.Round(samples[index] / Math.Max(scale, 0.001) * dots), 0, dots);
            }

            var rows = new string[height];

            for (int r = 0; r < height; r++)
            {
                var line = new char[width];

                for (int c = 0; c < width; c++)
                {
                    var bits = 0;

                    for (int sub = 0; sub < 4; sub++)
                    {
                        var above = (height - 1 - r) * 4 + (3 - sub);

                        if (above < filled[c * 2])
                            bits |= _left[sub];

                        if (above < filled[c * 2 + 1])
                            bits |= _right[sub];
                    }

                    line[c] = (char)(0x2800 + bits);
                }
                rows[r] = new string(line);
            }
            return rows;
        }

        internal static string Crop(string text, int max)
        {
            var flat = string.Concat(text.Select(x => char.IsControl(x) ? ' ' : x));

            return flat.Length <= max
                ? flat
                : flat[..Math.Max(1, max - 1)] + "…";
        }
    }
}