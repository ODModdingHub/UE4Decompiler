using Serilog;
using UE4Decompiler.Core.Mcp;
using UE4Decompiler.Core.Services;

namespace UE4Decompiler.Cli.CommandHandlers;

public static class McpHandler
{
    public static async Task<int> RunAsync(McpOptions opts, DecompilerService service)
    {
        // When running MCP over stdio, redirect all Serilog logging to stderr or log file
        // so that stdout is purely dedicated to JSON-RPC messages.
        var registry = new McpToolRegistry(service);

        var server = new McpServer(registry, Console.In, Console.Out);
        await server.RunAsync();
        return ExitCodes.Success;
    }
}
