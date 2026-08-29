using Docker.DotNet.Models;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Text;
using Color = Spectre.Console.Color;

namespace ThetaNexus.Shared
{
    internal class UI
    {
        internal static (string Text, Color Colour) Glyph(ContainerListResponse container)
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

        internal static string Compose((string Text, Color? Colour)[] cells, int body, bool isSelected)
        {
            var plain = string.Concat(cells.Select(x => x.Text));

            var markup = string.Concat(cells.Select(x => x.Colour is null
                ? Markup.Escape(x.Text)
                : $"[{x.Colour.Value.ToMarkup()}]{Markup.Escape(x.Text)}[/]"));

            if (plain.Length < body)
                markup += new string(' ', body - plain.Length);

            return isSelected ? $"[on #263041]{markup}[/]" : markup;
        }

        internal static string Spread((string Text, Color? Colour)[] items, int body)
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
    }
}