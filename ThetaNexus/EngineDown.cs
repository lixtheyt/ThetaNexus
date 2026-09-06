using Docker.DotNet;
using Spectre.Console;
using Color = Spectre.Console.Color;

namespace ThetaNexus
{
    internal static class EngineDown
    {
        internal static async Task<bool> Display(Exception ex)
        {
            var root = ex;

            while (root.InnerException is not null)
                root = root.InnerException;

            var cause = (ex as DockerApiException ?? root as DockerApiException, root) switch
            {
                ({ } api, _) => $"The engine answered with {(int)api.StatusCode} {api.StatusCode}, so it is running but refused the request. This usually means the client is newer than the engine or that the engine is paused.",
                (_, TimeoutException) => "The named pipe does not exist, which means Docker Desktop is not running. Start it and this screen moves on by itself.",
                (_, OperationCanceledException) => "The pipe is there but the engine did not answer in time. Docker Desktop is most likely still starting, or its WSL backend is down.",
                _ => "The engine could not be reached, and the reason is not one this screen recognises. The red line above is the raw text from the library."
            };

            var raw = $"{root.GetType().Name}: {root.Message}";
            var quit = false;

            AnsiConsole.Clear();

            await AnsiConsole.Live(new Markup(" "))
                .StartAsync(async ctx =>
                {
                    while (true)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);

                            quit = key.Key == ConsoleKey.Q;

                            if (key.Key is ConsoleKey.Q or ConsoleKey.R)
                                return;
                        }

                        var lines = new List<string>
                        {
                            "[bold]The Docker engine is not responding.[/]",
                            string.Empty,
                            $"[{Color.Grey35.ToMarkup()}]{Markup.Escape(Engine.Pipe)}[/]"
                        };

                        foreach (var wrapped in Wrap(raw, 68))
                            lines.Add($"[{Color.Red3.ToMarkup()}]{Markup.Escape(wrapped)}[/]");

                        lines.Add(string.Empty);

                        foreach (var wrapped in Wrap(cause, 68))
                            lines.Add($"[{Color.Grey.ToMarkup()}]{Markup.Escape(wrapped)}[/]");

                        lines.Add(string.Empty);
                        lines.Add($"[{Color.SteelBlue1.ToMarkup()}]r[/] try again     [{Color.SteelBlue1.ToMarkup()}]q[/] quit");

                        var top = Math.Max(0, (Console.WindowHeight - lines.Count - 4) / 2);

                        ctx.UpdateTarget(new Padder(Align.Center(new Panel(new Rows(lines.Select(x => new Markup(x.Length == 0 ? " " : x))))
                        {
                            Border = BoxBorder.Square,
                            BorderStyle = new Style(Color.Grey35),
                            Padding = new Padding(3, 1, 3, 1)
                        }), new Padding(0, top, 0, 0)));

                        ctx.Refresh();

                        await Task.Delay(100);
                    }
                });

            AnsiConsole.Clear();

            return !quit;
        }

        private static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            var line = string.Empty;

            foreach (var word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line);
                    line = string.Empty;
                }

                line += line.Length > 0 ? " " + word : word;
            }

            if (line.Length > 0)
                lines.Add(line);

            return lines;
        }
    }
}