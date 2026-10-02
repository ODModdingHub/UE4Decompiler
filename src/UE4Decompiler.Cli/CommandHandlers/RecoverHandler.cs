using Newtonsoft.Json;
using Spectre.Console;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class RecoverHandler
{
    public static async Task<int> RunAsync(RecoverOptions opts, IDecompilerService service)
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
                MappingPath = opts.Mapping,
                AesKey = opts.AesKey,
                SkipBlueprints = opts.SkipBlueprints,
                FullRecovery = opts.FullRecovery,
                NoMediaExport = opts.NoMedia,
                EmitStubs = opts.EmitStubs,
                DryRun = opts.DryRun,
                DangerBpGraph = opts.DangerBpGraph,
                MapTemplate = opts.Template,
                CubePath = opts.Cube,
                MaxDegreeOfParallelism = opts.Threads,
                Verbose = opts.Verbose
            };

            RecoveryReport report;

            if (opts.Quiet || opts.Json)
            {
                report = await service.RecoverAsync(opts.Input, opts.Output, decompileOpts);
            }
            else
            {
                report = await AnsiConsole.Progress()
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                             new RemainingTimeColumn(), new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("[green]Recovering Assets[/]", maxValue: 100);
                        var progress = new Progress<RecoveryProgress>(p =>
                        {
                            task.Description = $"[green]{Markup.Escape(p.Stage)}[/] [grey]({p.ProcessedCount:N0}/{p.TotalCount:N0})[/]";
                            task.Value = p.Percentage;
                        });

                        return await service.RecoverAsync(opts.Input, opts.Output, decompileOpts, progress);
                    });
            }

            if (opts.Json)
            {
                Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented));
                return report.FailedCount > 0 ? ExitCodes.PartialRecovery : ExitCodes.Success;
            }

            // Print summary table
            var table = new Table().Title(opts.DryRun ? "[yellow]Dry-run Simulation[/]" : "[green]Recovery Summary[/]");
            table.AddColumn("Fidelity");
            table.AddColumn(new TableColumn("Count").RightAligned());

            foreach (var (fidelity, count) in report.ByFidelity.OrderBy(k => k.Key))
            {
                table.AddRow(fidelity, count.ToString("N0"));
            }
            table.AddRow("[bold]Total Packages[/]", $"[bold]{report.TotalAssets:N0}[/]");
            AnsiConsole.Write(table);

            AnsiConsole.MarkupLine($"[bold]Elapsed:[/] {report.ElapsedTime.TotalSeconds:F2}s");
            AnsiConsole.MarkupLine($"[bold]Recovered:[/] [green]{report.RecoveredCount:N0}[/], [bold]Partial:[/] [yellow]{report.PartialCount:N0}[/], [bold]Failed:[/] [red]{report.FailedCount:N0}[/]");

            if (report.FailedCount > 0 && report.RecoveredCount > 0)
                return ExitCodes.PartialRecovery;

            if (report.FailedCount > 0 && report.RecoveredCount == 0)
                return ExitCodes.ParseFailure;

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Recovery operation cancelled.[/]");
            return ExitCodes.GeneralFailure;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error during recovery:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GeneralFailure;
        }
    }
}
