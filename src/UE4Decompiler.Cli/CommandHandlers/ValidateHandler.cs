using Newtonsoft.Json;
using Spectre.Console;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class ValidateHandler
{
    public static async Task<int> RunAsync(ValidateOptions opts, IDecompilerService service)
    {
        try
        {
            var targetDir = Path.GetFullPath(opts.Directory);
            if (!Directory.Exists(targetDir))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory '{Markup.Escape(targetDir)}' does not exist.");
                return ExitCodes.UnsupportedInput;
            }

            var report = await service.ValidateAsync(targetDir);

            if (opts.Json)
            {
                Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented));
                return report.IsAllValid ? ExitCodes.Success : ExitCodes.ValidationFailure;
            }

            var table = new Table().Title($"[bold]Validation Report for {Markup.Escape(targetDir)}[/]");
            table.AddColumn("File");
            table.AddColumn("Type");
            table.AddColumn("Status");
            table.AddColumn("Details / Checks");

            foreach (var item in report.Items.Take(opts.Quiet ? 50 : 200))
            {
                var status = item.IsValid ? "[green]VALID[/]" : "[red]INVALID[/]";
                var detail = item.IsValid
                    ? $"[grey]{string.Join(", ", item.ChecksPassed)}[/]"
                    : $"[red]{Markup.Escape(item.ErrorMessage ?? "Unknown validation failure")}[/]";

                table.AddRow(
                    Markup.Escape(Path.GetFileName(item.FilePath)),
                    item.AssetType,
                    status,
                    detail
                );
            }

            AnsiConsole.Write(table);

            AnsiConsole.MarkupLine($"[bold]Total Checked:[/] {report.TotalChecked:N0} | [green]Valid:[/] {report.ValidCount:N0} | [red]Invalid:[/] {report.InvalidCount:N0}");

            return report.IsAllValid ? ExitCodes.Success : ExitCodes.ValidationFailure;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Validation failed:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GeneralFailure;
        }
    }
}
