using Spectre.Console;
using UE4Decompiler.Core;
using UE4Decompiler.Core.Services;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class GraphHandler
{
    public static async Task<int> RunAsync(GraphOptions opts)
    {
        return await Task.Run(() =>
        {
            try
            {
                var aesKey = AesKeyResolver.FromHex(opts.AesKey);
                var hintedGame = VersionDetector.FromHint(opts.Engine);
                var mountGame = hintedGame ?? CUE4Parse.UE4.Versions.EGame.GAME_UE4_27;

                using var extractor = new PakExtractor(opts.Input, mountGame, aesKey, readScriptData: true);
                var parser = new AssetParser(extractor.Provider);

                var targetFile = extractor.Provider.Files.Values.FirstOrDefault(f =>
                    f.Path.Contains(opts.AssetPath, StringComparison.OrdinalIgnoreCase) &&
                    (f.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) ||
                     f.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase)));

                if (targetFile == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Could not find asset matching '{Markup.Escape(opts.AssetPath)}' in mounted container.");
                    return ExitCodes.UnsupportedInput;
                }

                var parsed = parser.Parse(targetFile);
                if (parsed == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Failed to parse package '{Markup.Escape(targetFile.Path)}'.");
                    return ExitCodes.ParseFailure;
                }

                var decompiler = new BlueprintDecompiler();
                string output;

                var fmt = opts.Format.ToLowerInvariant();
                switch (fmt)
                {
                    case "dot":
                    case "graphviz":
                        output = decompiler.ToGraphvizDot(parsed, opts.Function);
                        break;
                    case "mermaid":
                    case "mmd":
                        output = decompiler.ToMermaid(parsed, opts.Function);
                        break;
                    case "pseudo":
                    default:
                        output = decompiler.ToPseudoBlueprint(parsed, opts.Function);
                        break;
                }

                if (!string.IsNullOrWhiteSpace(opts.OutputFile))
                {
                    var fullOut = Path.GetFullPath(opts.OutputFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
                    File.WriteAllText(fullOut, output);
                    AnsiConsole.MarkupLine($"[green]Graph written to:[/] {Markup.Escape(fullOut)}");
                }
                else
                {
                    Console.WriteLine(output);
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Decompilation failed:[/] {Markup.Escape(ex.Message)}");
                return ExitCodes.GeneralFailure;
            }
        });
    }
}
