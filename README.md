# UE4Decompiler

[![CI](https://github.com/alphasayshello/UE4Decompiler/actions/workflows/ci.yml/badge.svg)](https://github.com/alphasayshello/UE4Decompiler/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey.svg)]()

A polished, cross-platform Unreal Engine 4 and Unreal Engine 5 asset recovery and decompilation pipeline for Windows, Linux, and macOS. Built with .NET 8, [Spectre.Console](https://spectreconsole.net/), and [Avalonia UI](https://avaloniaui.net/).

UE4Decompiler restores packaged game archives (`.pak`, `.utoc/.ucas` IoStore containers, and cooked `.uasset` files) into clean, editable Unreal Engine projects complete with `.uproject`, project configurations, plugin mounts, native C++ stub modules, and standard digital content creation interchange assets (glTF 2.0, PNG, WAV, JSON IR).

---

## Key Features

- **Cross-Platform**: First-class support for Windows (x64/arm64), Linux, and macOS (Apple Silicon & Intel).
- **Dual Interfaces**:
  - **Modern CLI**: Rich command-line interface with subcommands (`inspect`, `scan`, `recover`, `export`, `graph`, `validate`, `doctor`, `capabilities`, `version`) and full backward compatibility with legacy flags.
  - **Avalonia Desktop GUI**: Dedicated desktop application featuring a container scanner, live asset browser with filters, real-time recovery progress queue, health diagnostics, and preferences.
- **Model Context Protocol (MCP) Toolkit**:
  - Built-in stdio MCP server (`ue4decompiler mcp`) supporting Claude Desktop, Cursor, and Antigravity.
  - Interactive tools for container inspection, asset search, Blueprint bytecode decompilation, project recovery, and system diagnostics directly from AI developer environments.
- **Unreal Engine 4 & 5 Parity**:
  - Full support for traditional `.pak` containers and modern UE5 IoStore (`.utoc/.ucas`) Zen containers.
  - Unversioned property schema restoration via `.usmap` mapping files.
  - UE 4.21 editor-loadable uncooked binary package writer (`FPackageFileSummary`, `FCrc` serialized name hashes, `FRawMesh` static mesh payloads).
  - High-fidelity JSON Intermediate Representation (IR) + glTF/PNG media export for UE 5.0 through 5.5.
  - Advanced engine serialization fidelity: UE trailing numeric split (`FNameSplit`), case-canonical package path mapping (`PackagePathCanon`), and map built lighting registry serialization (`BuiltDataWriter`).
- **Blueprint VM Decompiler & Node Emitter**:
  - Disassembles Kismet bytecode into human-readable Pseudo-Blueprint source code.
  - Exports visual control-flow graphs in Graphviz DOT and Mermaid diagram formats.
  - Full `KismetGraphDecompiler` translating raw bytecodes into rich data-flow and execution pin topologies.
  - `BlueprintNodeEmitter` synthesizing native editor K2Nodes (`K2Node_Event`, `K2Node_CallFunction`, `K2Node_VariableGet`, `K2Node_IfThenElse`, etc.) directly into uncooked packages.
  - Synthesizes clean editor-loadable Blueprint stubs to ensure projects open smoothly without engine crashes.
- **C++ Native Stub Generation**:
  - Scans import tables for referenced native game classes and generates compilable C++ modules (`.Build.cs`, `.h`, `.cpp`) with reflected `UCLASS()` and `UPROPERTY()` macros.
- **Automated Validation & Diagnostics**:
  - Built-in `validate` command checks package tags, summaries, and deserialization.
  - Built-in `doctor` command verifies .NET runtime environment, SkiaSharp native libraries, container integrity, and write permissions.

---

## Quick Start

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 1. Build the Solution
```bash
git clone https://github.com/alphasayshello/UE4Decompiler.git
cd UE4Decompiler

dotnet build UE4Decompiler.sln -c Release
```

### 2. Run Automated Tests
```bash
dotnet test tests/UE4Decompiler.Tests/UE4Decompiler.Tests.csproj
```

### 3. Launch the Desktop GUI
```bash
dotnet run --project src/UE4Decompiler.Gui/UE4Decompiler.Gui.csproj
```

### 4. Use the CLI
```bash
# Verify system health
ue4decompiler doctor

# Inspect a container
ue4decompiler inspect ./Game/Content/Paks/Game-WindowsNoEditor.pak

# Recover full uncooked project
ue4decompiler recover ./Game/Content/Paks \
  --output ./RecoveredProject \
  --engine 4.27 \
  --aes-key 0x1234567890ABCDEF...

# Export media assets directly (glTF meshes, PNG textures)
ue4decompiler export ./Game/Content/Paks \
  --output ./ExportedMedia \
  --textures --meshes

# Decompile a Blueprint to Mermaid
ue4decompiler graph ./Game/Content/Paks /Game/Characters/BP_Hero --format mermaid
```

---

## CLI Command Overview

| Command | Purpose |
| :--- | :--- |
| `ue4decompiler inspect <file\|dir>` | Inspect container metadata, compression, encryption, and package counts |
| `ue4decompiler scan <dir>` | Scan a directory for containers and enumerate all virtual assets |
| `ue4decompiler recover <input> -o <dir>` | Recover assets into an uncooked Unreal Engine project |
| `ue4decompiler export <input> -o <dir>` | Export assets to standard interchange formats (GLB, PNG, WAV, JSON) |
| `ue4decompiler graph <input> <asset>` | Decompile Blueprint Kismet bytecode into graph visualization or pseudo-code |
| `ue4decompiler validate <output-dir>` | Validate recovered asset packages and models on disk |
| `ue4decompiler doctor` | Run system environment, native library, and container diagnostics |
| `ue4decompiler capabilities` | Display engine version and asset format capability matrix |
| `ue4decompiler version` | Display tool version and runtime information |
| `ue4decompiler mcp` | Launch Model Context Protocol stdio server for Claude Desktop, Cursor, Antigravity |

*Note: All legacy flags (e.g. `--input`, `--output`, `--version`, `--aes-key`, `--decode`, `--gen-mesh-all`, etc.) remain fully supported for backward compatibility.*

---

## Repository Structure

```
UE4Decompiler/
├── src/
│   ├── UE4Decompiler.Core/          # DecompilerService, Reconstructors, Writers, Containers, Services, MCP
│   ├── UE4Decompiler.Cli/           # Spectre.Console CLI application & subcommands
│   └── UE4Decompiler.Gui/           # Cross-platform Avalonia UI Desktop application
├── tests/
│   └── UE4Decompiler.Tests/         # Comprehensive xUnit test suite (71+ tests)
├── lib/                             # Direct CUE4Parse and native decoding assemblies
├── docs/                            # Comprehensive documentation suite
│   ├── getting-started.md           # Quickstart and setup guide
│   ├── cli.md                       # Complete CLI reference and exit codes
│   ├── gui.md                       # Desktop GUI walkthrough
│   ├── mcp.md                       # Model Context Protocol (MCP) AI integration guide
│   ├── ue4-support.md               # UE4 capabilities and uncooked package writer
│   ├── ue5-support.md               # UE5 IoStore, Zen format, and JSON IR pipeline
│   ├── mappings.md                  # .usmap unversioned property mapping guide
│   ├── blueprints.md                # Blueprint Kismet bytecode decompilation
│   ├── assets.md                    # Supported asset types and formats
│   ├── recovery.md                  # Full recovery lifecycle
│   ├── troubleshooting.md           # Common issues, FAQ, and solutions
│   └── architecture/                # System architecture and security model
├── scripts/                         # Build and test scripts (bash and PowerShell)
├── .github/workflows/               # GitHub Actions CI workflow
├── CONTRIBUTING.md                  # Contribution guidelines
├── CODE_OF_CONDUCT.md              # Contributor Covenant Code of Conduct
└── SECURITY.md                      # Security vulnerability reporting policy
```

---

## Documentation

For in-depth guides, see the [docs/](docs/) folder:
- [Getting Started](docs/getting-started.md)
- [CLI Reference](docs/cli.md)
- [Model Context Protocol (MCP)](docs/mcp.md)
- [Desktop GUI Guide](docs/gui.md)
- [Unreal Engine 4 Support](docs/ue4-support.md)
- [Unreal Engine 5 Support](docs/ue5-support.md)
- [Property Mappings (.usmap)](docs/mappings.md)
- [Blueprint Decompilation](docs/blueprints.md)
- [Asset Formats](docs/assets.md)
- [Troubleshooting & FAQ](docs/troubleshooting.md)
- [System Architecture](docs/architecture/architecture.md)

---

## License & Disclaimer

UE4Decompiler is released under the MIT License.

*Disclaimer: This software is intended for modding, educational purposes, interoperability research, and recovering assets from projects you own or have permission to analyze. Always respect the intellectual property rights and End User License Agreements (EULA) of content creators.*
