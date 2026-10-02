using Newtonsoft.Json;
using Spectre.Console;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class InspectHandler
{
    public static async Task<int> RunAsync(InspectOptions opts, IDecompilerService service)
    {
        try
        {
            var decompileOpts = new DecompileOptions
            {
                InputPath = opts.Input,
                OutputRoot = Path.GetTempPath(),
                Game = CUE4Parse.UE4.Versions.EGame.GAME_UE4_27,
                EngineAssociation = opts.Engine ?? "4.27",
                AesKey = opts.AesKey,
                MappingPath = opts.Mapping,
                Verbose = opts.Verbose
            };

            var scan = await service.ScanAsync(opts.Input, decompileOpts);

            if (opts.Json)
            {
                Console.WriteLine(JsonConvert.SerializeObject(scan, Formatting.Indented));
                return ExitCodes.Success;
            }

            var grid = new Grid();
            grid.AddColumn();
            grid.AddRow(new Rule("[yellow]Container Inspection Report[/]").LeftJustified());
            grid.AddRow(new Markup($"[bold]Target:[/] {Markup.Escape(scan.InputPath)}"));
            grid.AddRow(new Markup($"[bold]Detected Engine:[/] [cyan]{scan.EngineAssociation}[/] ([grey]{scan.DetectedGame}[/])"));
            grid.AddRow(new Markup($"[bold]Encryption:[/] {(scan.IsEncrypted ? "[red]Encrypted (AES key required)[/]" : "[green]Unencrypted / Key accepted[/]")}"));
            grid.AddRow(new Markup($"[bold]Requires Mapping (.usmap):[/] {(scan.RequiresMapping ? "[yellow]Yes[/]" : "[green]No[/]")}"));
            grid.AddRow(new Markup($"[bold]Total Discovered Assets:[/] [bold green]{scan.Assets.Count:N0}[/]"));

            AnsiConsole.Write(grid);
            AnsiConsole.WriteLine();

            if (scan.Containers.Count > 0)
            {
                var containerTable = new Table().Title("[bold]Discovered Containers[/]");
                containerTable.AddColumn("Type");
                containerTable.AddColumn("Path");
                containerTable.AddColumn("Mount");
                foreach (var c in scan.Containers)
                {
                    containerTable.AddRow(
                        c.Type.ToString(),
                        Markup.Escape(Path.GetFileName(c.FilePath)),
                        Markup.Escape(c.MountPoint)
                    );
                }
                AnsiConsole.Write(containerTable);
                AnsiConsole.WriteLine();
            }

            if (scan.AssetCountsByClass.Count > 0)
            {
                var assetTable = new Table().Title("[bold]Asset Class Breakdown[/]");
                assetTable.AddColumn("Asset Type");
                assetTable.AddColumn(new TableColumn("Count").RightAligned());
                foreach (var (cls, count) in scan.AssetCountsByClass.OrderByDescending(k => k.Value))
                {
                    assetTable.AddRow(cls, $"{count:N0}");
                }
                AnsiConsole.Write(assetTable);
            }

            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.UnsupportedInput;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Inspection failed:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GeneralFailure;
        }
    }
}
