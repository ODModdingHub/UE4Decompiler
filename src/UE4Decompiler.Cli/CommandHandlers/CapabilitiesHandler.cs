using Newtonsoft.Json;
using Spectre.Console;
using UE4Decompiler.Core.Abstractions;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class CapabilitiesHandler
{
    public static Task<int> RunAsync(CapabilitiesOptions opts, IDecompilerService service)
    {
        var caps = service.GetCapabilities();

        if (opts.Json)
        {
            Console.WriteLine(JsonConvert.SerializeObject(caps, Formatting.Indented));
            return Task.FromResult(ExitCodes.Success);
        }

        AnsiConsole.Write(new Rule("[yellow]UE4Decompiler Capability Matrix[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var engineTable = new Table().Title("[bold]Supported Unreal Engine Versions[/]");
        engineTable.AddColumn("Engine");
        engineTable.AddColumn("Read");
        engineTable.AddColumn("Uncooked .uasset Write");
        engineTable.AddColumn("Pipeline Strategy / Notes");

        foreach (var eng in caps.Engines)
        {
            engineTable.AddRow(
                eng.EngineVersion,
                eng.ReadSupported ? "[green]YES[/]" : "[red]NO[/]",
                eng.UncookedWriteSupported ? "[green]YES (Native 4.21+)[/]" : "[yellow]NO (JSON IR + Media Export)[/]",
                Markup.Escape(eng.Notes)
            );
        }
        AnsiConsole.Write(engineTable);
        AnsiConsole.WriteLine();

        var containerTable = new Table().Title("[bold]Supported Container Formats[/]");
        containerTable.AddColumn("Format");
        containerTable.AddColumn("Supported");
        containerTable.AddColumn("Notes");

        foreach (var c in caps.Containers)
        {
            containerTable.AddRow(
                c.ContainerName,
                c.Supported ? "[green]YES[/]" : "[red]NO[/]",
                Markup.Escape(c.Notes)
            );
        }
        AnsiConsole.Write(containerTable);
        AnsiConsole.WriteLine();

        var assetTable = new Table().Title("[bold]Supported Asset Classes & Recovery Methods[/]");
        assetTable.AddColumn("Asset Class");
        assetTable.AddColumn("Capability Tier");
        assetTable.AddColumn("Recovery Method");
        assetTable.AddColumn("Export Formats");

        foreach (var a in caps.Assets)
        {
            assetTable.AddRow(
                a.AssetClass,
                $"[cyan]{a.Capability}[/]",
                Markup.Escape(a.RecoveryMethod),
                $"[grey]{Markup.Escape(a.Formats)}[/]"
            );
        }
        AnsiConsole.Write(assetTable);

        return Task.FromResult(ExitCodes.Success);
    }
}
