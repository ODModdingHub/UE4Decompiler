using CommandLine;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using UE4Decompiler.Cli.CommandHandlers;
using UE4Decompiler.Core.Services;

namespace UE4Decompiler.Cli;

public static class Program
{
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "inspect", "scan", "recover", "export", "graph", "validate", "doctor", "capabilities", "version"
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintWelcomeBanner();
            PrintHelp();
            return ExitCodes.Success;
        }

        var isNoColor = args.Any(a => a.Equals("--no-color", StringComparison.OrdinalIgnoreCase));
        if (isNoColor)
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        }

        var isVerbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || a.Equals("-v", StringComparison.OrdinalIgnoreCase));
        var logFile = ExtractOptionValue(args, "--log-file");

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(isVerbose ? LogEventLevel.Debug : LogEventLevel.Information);

        if (!args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            logConfig.WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            logConfig.WriteTo.File(logFile);
        }

        Log.Logger = logConfig.CreateLogger();

        var service = new DecompilerService();

        var firstArg = args[0].TrimStart('-', '/');

        // Check if modern verb was requested
        if (Verbs.Contains(firstArg))
        {
            return await Parser.Default.ParseArguments<
                InspectOptions,
                ScanOptions,
                RecoverOptions,
                ExportOptions,
                GraphOptions,
                ValidateOptions,
                DoctorOptions,
                CapabilitiesOptions,
                VersionOptions>(args)
                .MapResult(
                    (InspectOptions opts) => InspectHandler.RunAsync(opts, service),
                    (ScanOptions opts) => ScanHandler.RunAsync(opts, service),
                    (RecoverOptions opts) => RecoverHandler.RunAsync(opts, service),
                    (ExportOptions opts) => ExportHandler.RunAsync(opts, service),
                    (GraphOptions opts) => GraphHandler.RunAsync(opts),
                    (ValidateOptions opts) => ValidateHandler.RunAsync(opts, service),
                    (DoctorOptions opts) => DoctorHandler.RunAsync(opts, service),
                    (CapabilitiesOptions opts) => CapabilitiesHandler.RunAsync(opts, service),
                    (VersionOptions opts) => RunVersionAsync(opts),
                    _ => Task.FromResult(ExitCodes.InvalidArguments)
                );
        }

        // Backward compatibility mode: legacy flags
        return await Task.Run(() =>
        {
            using var legacyParser = new Parser(with =>
            {
                with.AutoVersion = false;
                with.HelpWriter = Console.Error;
                with.CaseInsensitiveEnumValues = true;
            });

            return legacyParser.ParseArguments<LegacyCliOptions>(args)
                .MapResult(LegacyHandler.Run, _ => ExitCodes.InvalidArguments);
        });
    }

    private static Task<int> RunVersionAsync(VersionOptions opts)
    {
        if (opts.Json)
        {
            var info = new
            {
                Application = "UE4Decompiler",
                Version = "2.0.0",
                Runtime = Environment.Version.ToString(),
                OS = Environment.OSVersion.ToString(),
                EnginesSupported = new[] { "UE 4.10 - 4.27", "UE 5.0 - 5.5" }
            };
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(info, Newtonsoft.Json.Formatting.Indented));
            return Task.FromResult(ExitCodes.Success);
        }

        AnsiConsole.MarkupLine("[bold yellow]UE4Decompiler[/] version [bold cyan]2.0.0[/]");
        AnsiConsole.MarkupLine("Unreal Engine 4 & 5 Asset Recovery Pipeline (.NET 8.0)");
        AnsiConsole.MarkupLine($"OS: [grey]{Environment.OSVersion}[/]");
        return Task.FromResult(ExitCodes.Success);
    }

    private static void PrintWelcomeBanner()
    {
        AnsiConsole.Write(
            new FigletText("UE4Decompiler")
                .LeftJustified()
                .Color(Color.Yellow));
        AnsiConsole.MarkupLine("[bold cyan]Unreal Engine 4 & 5 Asset Recovery Pipeline[/] (Version 2.0.0)");
        AnsiConsole.MarkupLine("Cross-platform decompilation for Windows, Linux, and macOS.");
        AnsiConsole.WriteLine();
    }

    private static void PrintHelp()
    {
        var table = new Table().Title("[bold]Available Commands[/]");
        table.AddColumn("Command");
        table.AddColumn("Description");

        table.AddRow("[bold cyan]inspect[/] <input>", "Inspect container metadata, compression, encryption, and package counts.");
        table.AddRow("[bold cyan]scan[/] <dir>", "Scan a folder for Unreal Engine containers and enumerate assets.");
        table.AddRow("[bold cyan]recover[/] <input> -o <dir>", "Recover assets from containers into an uncooked Unreal project.");
        table.AddRow("[bold cyan]export[/] <input> -o <dir>", "Export assets directly to standard formats (GLB, PNG, WAV, JSON).");
        table.AddRow("[bold cyan]graph[/] <input> <asset>", "Decompile Blueprint Kismet bytecode into graph (DOT/Mermaid) or pseudo-code.");
        table.AddRow("[bold cyan]validate[/] <output-dir>", "Validate recovered assets in a project directory.");
        table.AddRow("[bold cyan]doctor[/]", "Run environment, dependency, and storage diagnostics.");
        table.AddRow("[bold cyan]capabilities[/]", "Display the engine and asset format capability matrix.");
        table.AddRow("[bold cyan]version[/]", "Display version and build information.");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\nRun [yellow]ue4decompiler <command> --help[/] for detailed options on any command.");
    }

    private static string? ExtractOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
