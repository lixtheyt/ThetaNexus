using Spectre.Console;
using System.Reflection;
using System.Text;
using ThetaNexus.Screens;
using ThetaNexus.Api;

namespace ThetaNexus
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;

            Console.CancelKeyPress += (_, _) => Console.CursorVisible = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.CursorVisible = true;

            AnsiConsole.Clear();

            using var client = Engine.Connect();

            var assembly = Assembly.GetExecutingAssembly();

            await using var stream = assembly.GetManifestResourceStream("ThetaNexus.Figlet_Fonts.Bloody.flf")
                                     ?? throw new InvalidOperationException($"Bloody.flf is not in the assembly. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

            var font = FigletFont.Load(stream);

            var top = Math.Max(0, (Console.WindowHeight - font.Height - 2) / 2);

            AnsiConsole.Write(new Padder(Align.Center(new Rows(
                new Grid()
                    .AddColumn(new GridColumn { Padding = new Padding(0, 0, 0, 0), NoWrap = true, Width = 43 })
                    .AddColumn(new GridColumn { Padding = new Padding(0, 0, 0, 0), NoWrap = true, Width = 45 })
                    .AddRow(
                        new FigletText(font, "Theta").Color(Color.Blue3_1).LeftJustified(),
                        new FigletText(font, "Nexus").Color(Color.Red3_1).LeftJustified()),
                new Markup(string.Empty),
                Align.Center(new Markup($"[{Color.Grey35}]created by lix[/]")))), new Padding(0, top, 0, 0)));

            Thread.Sleep(1500);

            AnsiConsole.Clear();

            await MainList.Display(client);
        }
    }
}