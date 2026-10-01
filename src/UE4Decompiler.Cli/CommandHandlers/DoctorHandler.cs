using Newtonsoft.Json;
using Spectre.Console;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class DoctorHandler
{
    public static async Task<int> RunAsync(DoctorOptions opts, IDecompilerService service)
    {
        try
        {
            var report = await service.RunDoctorAsync(opts.Input, opts.Output);

            if (opts.Json)
            {
                Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented));
                return report.HasErrors ? ExitCodes.GeneralFailure : ExitCodes.Success;
            }

            var table = new Table().Title("[bold]System & Environment Health Diagnostics[/]");
            table.AddColumn("Category");
            table.AddColumn("Check");
            table.AddColumn("Status");
            table.AddColumn("Message");
            table.AddColumn("Suggested Fix");

            foreach (var item in report.Items)
            {
                var status = item.Level switch
                {
                    DiagnosticLevel.Pass => "[green]PASS[/]",
                    DiagnosticLevel.Info => "[cyan]INFO[/]",
                    DiagnosticLevel.Warning => "[yellow]WARN[/]",
                    DiagnosticLevel.Error => "[bold red]FAIL[/]",
                    _ => item.Level.ToString()
                };

                table.AddRow(
                    item.Category,
                    item.CheckName,
                    status,
                    Markup.Escape(item.Message),
                    item.SuggestedFix != null ? $"[cyan]{Markup.Escape(item.SuggestedFix)}[/]" : "-"
                );
            }

            AnsiConsole.Write(table);

            if (report.HasErrors)
            {
                AnsiConsole.MarkupLine("[bold red]Diagnostics found errors that may affect decompiler operation.[/]");
                return ExitCodes.GeneralFailure;
            }

            if (report.HasWarnings)
            {
                AnsiConsole.MarkupLine("[bold yellow]Diagnostics passed with warnings.[/]");
                return ExitCodes.Success;
            }

            AnsiConsole.MarkupLine("[bold green]All environment and capability checks passed successfully![/]");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Doctor run failed:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GeneralFailure;
        }
    }
}
