using Newtonsoft.Json;
using Spectre.Console;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class ScanHandler
{
    public static async Task<int> RunAsync(ScanOptions opts, IDecompilerService service)
    {
        try
        {
            var decompileOpts = new DecompileOptions
            {
                InputPath = opts.Directory,
                OutputRoot = Path.GetTempPath(),
                Game = CUE4Parse.UE4.Versions.EGame.GAME_UE4_27,
                EngineAssociation = opts.Engine ?? "4.27",
                FilterGlob = opts.Filter,
                AesKey = opts.AesKey,
                Verbose = opts.Verbose
            };

            var scan = await service.ScanAsync(opts.Directory, decompileOpts);

            if (opts.Json)
            {
                Console.WriteLine(JsonConvert.SerializeObject(scan.Assets, Formatting.Indented));
                return ExitCodes.Success;
            }

            var table = new Table().Title($"[bold]Discovered Assets ({scan.Assets.Count:N0})[/]");
            table.AddColumn("Virtual Path");
            table.AddColumn("Type");
            table.AddColumn("Mount");
            table.AddColumn(new TableColumn("Size").RightAligned());

            foreach (var a in scan.Assets.Take(opts.Quiet ? 50 : 250))
            {
                table.AddRow(
                    Markup.Escape(a.VirtualPath),
                    a.Extension.ToUpperInvariant(),
                    a.MountPoint,
                    $"{a.Size:N0} B"
                );
            }

            if (scan.Assets.Count > 250 && !opts.Quiet)
            {
                table.Caption = new TableTitle($"[grey]Showing first 250 of {scan.Assets.Count:N0} assets. Use --json to dump all.[/]");
            }

            AnsiConsole.Write(table);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Scan failed:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GeneralFailure;
        }
    }
}
