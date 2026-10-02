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

### `ue_diff_containers`
Compare two Unreal Engine containers or patch builds to identify added, removed, modified, or resized assets.

- **Parameters:**
  - `containerA` (string, required): Base container file or paks folder (e.g. Patch 1.0).
  - `containerB` (string, required): New container file or paks folder to compare against (e.g. Patch 1.1).
  - `engineA` (string, optional): Engine version hint for container A.
  - `engineB` (string, optional): Engine version hint for container B.
  - `aesKeyA` (string, optional): AES key for container A.
  - `aesKeyB` (string, optional): AES key for container B.

### `ue_batch_export`
Batch export uncooked assets (Textures to PNG, Static/Skeletal Meshes to glTF, SoundWave to WAV/OGG, Blueprints to pseudo-C++) directly to disk.

- **Parameters:**
  - `containerPath` (string, required): Path to container (.pak, .utoc) or paks folder.
  - `exportDirectory` (string, required): Destination directory on disk where exported files will be written.
  - `filter` (string, optional): Path filter or wildcard (e.g. `Textures/UI`, `*Hero*`).
  - `engineVersion` (string, optional): Engine version hint (e.g. `4.27`, `5.3`, `5.4`, `5.5`).
  - `aesKey` (string, optional): AES key.

### `ue_extract_metadata`
Extract detailed package metadata, UObject export table, import dependencies, and serialized property tags as JSON.

- **Parameters:**
  - `containerPath` (string, required): Path to container (.pak, .utoc) or paks folder.
  - `assetPath` (string, required): Virtual package path (e.g. `/Game/Characters/BP_Player`).
  - `engineVersion` (string, optional): Engine version hint.
  - `aesKey` (string, optional): AES key.

### `ue_generate_cpp_headers`
Reconstruct native C++ module headers (`UCLASS`, `USTRUCT`, `UENUM`) from cooked packages with `UPROPERTY` and `UFUNCTION` signatures for compiling in Visual Studio / Rider / Xcode.

- **Parameters:**
  - `containerPath` (string, required): Path to container or paks folder.
  - `outputDirectory` (string, required): Destination directory for C++ header files.
  - `moduleName` (string, optional): Module name (default: `RecoveredGame`).
  - `engineVersion` (string, optional): Engine version hint.
  - `aesKey` (string, optional): AES key.

### `ue_iostore_info`
Inspect Unreal Engine 5 Zen Store and IoStore (`.utoc`/`.ucas`) container headers, compression blocks, chunk metadata, and encryption flags.

- **Parameters:**
  - `containerPath` (string, required): Path to `.utoc`, `.ucas`, or IoStore container file.
  - `aesKey` (string, optional): AES key.

### `ue_diagnose`
Run system diagnostics on .NET runtime, native compression codecs, AES decryption keys, and engine mappings.

- **Parameters:** None.

### `ue_capabilities`
Return the complete Unreal Engine version (UE 4.18 through UE 5.5) and asset format recovery capability matrix in structured JSON format.

- **Parameters:** None.

### `ue_inspect_asset`
Deeply inspect a single asset package inside a container, returning properties, exports, dependencies, lighting parameters, and material shader bindings.

- **Parameters:**
  - `containerPath` (string, required): Path to container (.pak, .utoc) or paks folder.
  - `assetPath` (string, required): Virtual asset package path (e.g. `/Game/Maps/MainMap` or `/Game/Materials/M_Metal`).
  - `aesKey` (string, optional): AES key.
  - `engineVersion` (string, optional): Engine version hint (e.g. `4.27`, `5.1`, `5.4`, `5.5`).

### `ue_export_asset`
Directly export a single asset package from a container to disk (glTF 2.0 for meshes, PNG for textures, WAV/OGG for audio, or raw JSON).

- **Parameters:**
  - `containerPath` (string, required): Path to container (.pak, .utoc) or paks folder.
  - `assetPath` (string, required): Virtual asset package path to export.
  - `outputFile` (string, required): Destination file path on disk.
  - `format` (string, optional): Export format (`auto`, `gltf`, `png`, `wav`, `cpp`, `uasset`). Default: `auto`.
  - `aesKey` (string, optional): AES key.
  - `engineVersion` (string, optional): Engine version hint.

### `ue_extract_lighting`
Extract all lighting actors, sky atmosphere, volumetric fog, clouds, post process settings, and built lighting data from a level map into structured JSON.

- **Parameters:**
  - `containerPath` (string, required): Path to container (.pak, .utoc) or paks folder.
  - `mapPath` (string, required): Virtual asset package path of the map (e.g. `/Game/Maps/Arena01`).
  - `outputJson` (string, optional): Output file path to write the lighting summary JSON.
  - `aesKey` (string, optional): AES key.
  - `engineVersion` (string, optional): Engine version hint.

---

## 4. Example Interaction Prompt

Once configured with an MCP client, you can ask prompts like:

> "Inspect the game archive at `./Game/Content/Paks/Game-WindowsNoEditor.pak` and list the Blueprint classes found."

The AI agent will invoke `ue_inspect` and `ue_search_assets` with `assetClass: "UBlueprint"` and present the results cleanly.

> "Decompile the event graph of `/Game/Core/BP_GameManager` into a Mermaid diagram."

The AI agent will invoke `ue_decompile_blueprint` with `format: "mermaid"` and render the interactive graph directly in your conversation.
