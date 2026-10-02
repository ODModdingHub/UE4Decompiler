using Spectre.Console;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class ExportHandler
{
    public static async Task<int> RunAsync(ExportOptions opts, IDecompilerService service)
    {
        try
        {
            var hintedGame = VersionDetector.FromHint(opts.Engine);
            var mountGame = hintedGame ?? CUE4Parse.UE4.Versions.EGame.GAME_UE4_27;

            var decompileOpts = new DecompileOptions
            {
                InputPath = opts.Input,
                OutputRoot = Path.GetFullPath(opts.Output),
                Game = mountGame,
                EngineAssociation = opts.Engine ?? VersionDetector.ToEngineAssociation(mountGame),
                FilterGlob = opts.Filter,
                AesKey = opts.AesKey,
                SkipBlueprints = true,
                NoMediaExport = false,
                Verbose = opts.Verbose
            };

            AnsiConsole.MarkupLine($"[cyan]Exporting media assets from[/] {Markup.Escape(opts.Input)} -> {Markup.Escape(opts.Output)}");

            var report = await service.RecoverAsync(opts.Input, opts.Output, decompileOpts);

            AnsiConsole.MarkupLine($"[green]Export complete:[/] {report.RecoveredCount:N0} assets exported to {Markup.Escape(opts.Output)}.");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Export failed:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GeneralFailure;
        }
    }
}
