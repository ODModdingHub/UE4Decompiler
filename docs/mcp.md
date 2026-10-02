# Model Context Protocol (MCP) Toolkit Guide

`UE4Decompiler` includes a built-in **Model Context Protocol (MCP)** server over standard input/output (`stdio`). This allows AI development environments (such as **Claude Desktop**, **Cursor**, **Antigravity**, and **VS Code MCP extensions**) to directly inspect Unreal Engine containers, decompile Blueprints, search assets, diagnose missing dependencies, and orchestrate full project recovery workflows.

---

## 1. Launching the MCP Server

The MCP server runs using the `mcp` verb:

```bash
ue4decompiler mcp
```

Or using dotnet:

```bash
dotnet run --project src/UE4Decompiler.Cli -- mcp
```

### Stdio Cleanliness Guarantee
The MCP server strictly conforms to the JSON-RPC 2.0 transport specification. In `mcp` mode, all standard application logging (Serilog diagnostics, warnings, and informational logs) is automatically routed to `stderr`, keeping `stdout` dedicated purely to valid JSON-RPC frames.

---

## 2. Configuration with AI Assistants

### A. Claude Desktop
Add `ue4decompiler` to your `claude_desktop_config.json`:

- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "ue4decompiler": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/UE4Decompiler/src/UE4Decompiler.Cli",
        "--",
        "mcp"
      ]
    }
  }
}
```

Or when installed/published as a binary:

```json
{
  "mcpServers": {
    "ue4decompiler": {
      "command": "/usr/local/bin/ue4decompiler",
      "args": ["mcp"]
    }
  }
}
```

### B. Cursor & VS Code
In your project or user settings (`.cursor/mcp.json` or VS Code MCP configuration):

```json
{
  "mcpServers": {
    "ue4decompiler": {
      "command": "ue4decompiler",
      "args": ["mcp"]
    }
  }
}
```

---

## 3. Available MCP Tools

The server automatically registers the following tools:

### `ue_inspect`
Inspect an Unreal Engine archive (`.pak`, `.utoc`, `.ucas`) or game directory to determine engine version, encryption, compression, mount points, and package inventory.

- **Parameters:**
  - `path` (string, required): Absolute or relative path to the container file or game directory.
  - `aesKey` (string, optional): 256-bit hexadecimal AES decryption key.
  - `engineVersion` (string, optional): Unreal Engine version hint (e.g. `4.21`, `4.27`, `5.1`, `5.3`).

### `ue_scan`
Scan a folder recursively for Unreal Engine containers and discover packages.

- **Parameters:**
  - `directory` (string, required): Directory path to scan for Unreal Engine containers.

### `ue_search_assets`
Search and filter assets inside containers by package path, asset name, or asset class.

- **Parameters:**
  - `path` (string, required): Path to the container or game directory.
  - `query` (string, optional): Wildcard or substring filter (e.g. `*Player*`, `MI_*`).
  - `assetClass` (string, optional): Class filter (e.g. `UBlueprint`, `UStaticMesh`, `UTexture2D`, `UMaterial`).
  - `maxResults` (integer, optional): Maximum number of results to return (default: 50).
  - `aesKey` (string, optional): Decryption key if container is encrypted.

### `ue_decompile_blueprint`
Decompile an Unreal Engine Blueprint asset's Kismet bytecode into high-level pseudo-C++, Graphviz DOT, or Mermaid diagram.

- **Parameters:**
  - `containerPath` (string, required): Path to the container or game directory.
  - `assetPath` (string, required): Virtual asset package path (e.g. `/Game/Characters/BP_Player`).
  - `format` (string, optional): Output format: `"cpp"`, `"dot"`, or `"mermaid"`. Default: `"cpp"`.
  - `function` (string, optional): Specific function name to decompile (or all functions if omitted).
  - `aesKey` (string, optional): Decryption key.

### `ue_recover_project`
Execute full asset recovery from Unreal Engine containers into an uncooked Unreal project workspace.

- **Parameters:**
  - `inputPath` (string, required): Source container file or folder containing PAKs.
  - `outputPath` (string, required): Destination directory for recovered project assets.
  - `engineVersion` (string, optional): Target engine version (default: `4.21`).
  - `filter` (string, optional): Asset path glob filter (e.g. `*/Maps/*`).
  - `aesKey` (string, optional): Optional AES key.
  - `mappingPath` (string, optional): Optional `.usmap` unversioned property mapping file.
  - `emitStubs` (boolean, optional): Generate compilable C++ stub modules for game native classes.

### `ue_diagnose`
Run system diagnostics on .NET runtime, native compression codecs, AES decryption keys, and engine mappings.

- **Parameters:** None.

### `ue_capabilities`
Return the complete Unreal Engine version and asset format recovery capability matrix in structured JSON format.

- **Parameters:** None.

---

## 4. Example Interaction Prompt

Once configured with an MCP client, you can ask prompts like:

> "Inspect the game archive at `./Game/Content/Paks/Game-WindowsNoEditor.pak` and list the Blueprint classes found."

The AI agent will invoke `ue_inspect` and `ue_search_assets` with `assetClass: "UBlueprint"` and present the results cleanly.

> "Decompile the event graph of `/Game/Core/BP_GameManager` into a Mermaid diagram."

The AI agent will invoke `ue_decompile_blueprint` with `format: "mermaid"` and render the interactive graph directly in your conversation.
