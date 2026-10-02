# Current State Audit: UE4Decompiler

**Date:** October 2026  
**Auditor:** Antigravity (Advanced Agentic Coding)  
**Target:** UE4Decompiler (.NET 8)

---

## Executive Summary

UE4Decompiler is an existing .NET 8 tool designed to recover Unreal Engine game assets from cooked shipping packages (`.pak`, `.utoc`/`.ucas`, `.uasset`, `.umap`) into an openable `.uproject` directory with recovered assets, JSON intermediate representations, extracted source media, and—for verified UE 4.21 assets—genuine editor-loadable uncooked `.uasset` binaries.

This audit evaluates the codebase's actual implementation against documented claims, categorizing supported Unreal Engine versions, container capabilities, asset reconstructors, Blueprints, package serialization, cross-platform compatibility, and testing state.

---

## Detailed Audit Findings: 23 Core Questions

### 1. What does the application currently do?
- **Core Pipeline:**
  1. Mounts container archives (`.pak`, `.utoc`/`.ucas`) or loose directories using CUE4Parse's `DefaultFileProvider`.
  2. Resolves AES-256 keys from user hex strings or an executable entropy scanner.
  3. Sniffs engine version hints or reads package headers.
  4. Scaffolds an Unreal project directory: extracts real `.uproject` and `Config/*.ini` from the container, patches `EngineAssociation`, sanitizes native modules, and cleans up plugin references.
  5. Enumerates packages and passes them through `AssetParser`.
  6. Routes parsed assets to one of five reconstructors:
     - `TextureReconstructor` (decodes top mip to PNG)
     - `MeshReconstructor` (converts LOD0 static/skeletal mesh to glTF 2.0 `.glb`)
     - `MaterialReconstructor` (extracts expression graph, pins, and parameters to JSON)
     - `BlueprintReconstructor` (pattern-matches Kismet bytecode to K2 nodes, walks expressions to JSON AST)
     - `LevelReconstructor` (extracts actors, components, transforms, streaming level refs)
  7. For UE 4.21 assets, attempts editor-loadable binary generation via `UncookedPackageWriter`, `SynthPackageWriter`, `TextureWriter`, `MaterialWriter`, or `BlueprintGraphBuilder`.
  8. Emits a JSON model sidecar (`.json`) for every asset as the recovery source of truth.
  9. Generates compilable C++ stub modules (`StubModuleGenerator`) for referenced game-native `/Script/` classes so the editor can resolve parent classes without crashing.
  10. Produces a decompile report (`decompile-report.json`) and terminal summary.

### 2. What UE4 versions are actually supported?
- **Package Parsing (Read):**
  - Full reading support for UE 4.0 through 4.27 via CUE4Parse.
- **Engine Version Detection:**
  - `VersionDetector` contains explicit thresholds for `4.24` (`ADDED_SOFT_OBJECT_PATH`), `4.26` (`FIX_WIDE_STRING_CRC`), and `4.27` (`CORRECT_LICENSEE_FLAG`), defaulting to `4.27` if unversioned.
- **Uncooked Package Writing (Write):**
  - **Strictly UE 4.21.**
  - `UncookedPackageWriter` hardcodes `FileVersionUE4 = 517`, `LegacyFileVersion = -7`, and an explicit 4.21 registered custom version container with 22 `Dev-*` GUIDs plus `Release=20`.
  - Name-table hashing in `FCrc` implements the 4.21 algorithm (`Strihash_DEPRECATED` for non-case-preserving and `StrCrc32` for case-preserving).
  - Attempting to use this writer for UE 4.25–4.27 causes the editor to report package version mismatches or serial size corruption.

### 3. What UE5 versions are actually supported?
- **Package Parsing (Read):**
  - CUE4Parse includes types for UE 5.0 through 5.5 (`EGame.GAME_UE5_0` to `GAME_UE5_5`).
  - `VersionDetector` recognizes `5.0`, `5.1`, `5.2`, `5.3`, `5.4`, `5.5` hints.
  - UE5 version sniffing detects `FileVersionUE5 >= DATA_RESOURCES` (`5.3`) and `LARGE_WORLD_COORDINATES` (`5.0`).
- **Uncooked Package Writing (Write):**
  - **Zero UE5 editor-loadable binary writing.** The package writer has no support for UE5 package headers, Zen loaders, new export map serialization, or UE5 custom versions.
- **Limitations:**
  - Cooked UE5 packages use unversioned properties (`PKG_UnversionedProperties`). Without `.usmap` mapping files, CUE4Parse cannot deserialize them. The existing code did not wire up mapping providers, causing UE5 property deserialization to fail on unversioned content.

### 4. Does IoStore work?
- **Reading:** Supported through CUE4Parse's `DefaultFileProvider`. When `.utoc` and `.ucas` container pairs are located in the input directory, CUE4Parse mounts them and enumerates `IoPackage` instances.
- **Writing:** **Not supported.** `UncookedPackageWriter` explicitly bails on `IoPackage` (`if (asset.Package is not Package pkg) return false`). IoStore content falls back to placeholder headers and JSON sidecars.

### 5. Do .utoc/.ucas files work?
- Yes, for container ingestion via CUE4Parse when located in the mounted directory.
- `PakExtractor` previously used `SearchOption.TopDirectoryOnly`, missing `.utoc`/`.ucas` located in nested subdirectories.

### 6. Do mappings work?
- **No.** The existing codebase had no `--usmap` CLI flag, no mapping resolver, and never assigned `Provider.MappingsContainer`. This severely limited UE5 and unversioned UE4 support.

### 7. Does AES work?
- **Yes.**
  - Hex keys passed via `--aes-key` are parsed by `AesKeyResolver.FromHex` into `FAesKey`.
  - Keys are submitted for the null GUID (standard container key) and dynamic GUIDs.
  - `AesKeyResolver.ScanExecutableForKeys` implements Shannon-entropy heuristics over 32-byte chunks to discover embedded AES keys in shipped binaries.

### 8. How does engine-version detection work?
- **Hint:** If `--version` is passed (e.g. `4.27`, `5.2`), `VersionDetector.FromHint` maps it directly to `EGame`.
- **Default:** If omitted, mounts with `GAME_UE4_27`.
- **Sniffing:** `VersionDetector.DetectFromPackage` inspects the first successfully parsed package:
  - If `FileVersionUE5 > 0`, maps to UE 5.0 or 5.3.
  - If `FileVersionUE4 > 0`, checks against version gates (4.27, 4.26, 4.24).
  - If unversioned (`FileVersionUE4 == 0`), falls back to the mount default (`GAME_UE4_27`).

### 9. Which asset types are fully recovered?
- **Texture2D / TextureCube:** Decodes largest mip via CUE4Parse-Conversion into source PNG; in 4.21, writes editor-loadable `UTexture2D` with inline `FTextureSource`.
- **Material / MaterialInstanceConstant:** Full expression node graph (positions, pins, scalar/vector/texture parameters) extracted to JSON model; in 4.21, synthesizes a valid default-lit `UMaterial` sampling the primary texture.
- **Pure Data Types (UE 4.21):** `DataTable`, `CurveFloat`, `CurveVector`, `CurveLinearColor`, `CurveTable`, `StringTable`, `UserDefinedEnum`, `UserDefinedStruct`, `SubsurfaceProfile` are fully round-tripped as editor-loadable `.uasset` files via `UncookedPackageWriter`.

### 10. Which asset types are partially recovered?
- **StaticMesh / SkeletalMesh:** Geometry (LOD0 vertices, indices, normals, tangents, UV channels, bones, weights, morph targets) exported to glTF 2.0 (`.glb`). In 4.21, static meshes can be cloned from an engine cube with `FRawMesh` binary injection.
- **World / Level (.umap):** Actor classes, components, transforms, and streaming references extracted to JSON model. In 4.21, placed actors can be synthesized onto an empty template map.
- **Generic Assets:** Deserialized export properties preserved in JSON sidecars.

### 11. Which asset types are metadata-only?
- Blueprints when `--skip-blueprints` is enabled.
- Assets whose bulk data is missing or streamed out without bulk archives.
- Unhandled native types where CUE4Parse can only read basic header metadata.

### 12. Which asset types fail?
- UE5 unversioned packages when no `.usmap` mapping file is available.
- Packages with unsupported custom compression.
- Complex Blueprints (Widget Blueprints, Anim Blueprints, deep bytecode graphs) when opened in Unreal Editor after naive cooked cloning (causes editor recursion crashes; gated behind `--dangerously-dump-bpgraph`).
- Maps attempting verbatim uncooked writes without a template.

### 13. How does Blueprint recovery actually work?
1. `PakExtractor` sets `Provider.ReadScriptData = true`.
2. `BlueprintReconstructor` finds `BlueprintGeneratedClass` exports and iterates over all `UFunction` exports.
3. For each function, walks `ScriptBytecode`.
4. `MapToK2Node` maps bytecode tokens to high-level K2 nodes (`UK2Node_CallFunction`, `UK2Node_VariableGet`, `UK2Node_VariableSet`, `UK2Node_IfThenElse`, `UK2Node_ExecutionSequence`, `UK2Node_Switch`, `UK2Node_FunctionResult`, `UK2Node_MakeStruct`).
5. Unknown tokens become `UK2Node_Comment` stubs with raw opcode operands.
6. `KismetWalker` reflects over expression fields, producing a normalized AST.
7. For UE 4.21 editor-loadable blueprints: `BlueprintGraphBuilder` clones cooked exports, patches `bCooked = false`, appends synthesized `UBlueprint`, `EdGraph`, `K2Node_Event`, and `K2Node_CallFunction` exports, serializes pins via `PinSerializer`, and connects execution wires.

### 14. What does `--full-recovery` actually do?
- Forces `Provider.ReadScriptData = true`.
- Invokes `KismetWalker.Walk` on every function bytecode array.
- Generates rich JSON IR containing:
  - Full opcode statement trees and operand names/types.
  - `SuperStruct` parent references.
  - `TargetSkeleton` for AnimBlueprints.
  - `AnimGraphNodeOrder` (export order for anim-graph LinkID resolution).
- Designed for consumption by the Unreal Editor `JsonAssetImport` commandlet.

### 15. What does the package writer actually support?
- **UncookedPackageWriter:** Copies tagged property payloads from cooked `.uasset` + `.uexp` into a single uncooked package. Rebuilds summary and link tables. Injects `AssetRegistry` records. Patches `bCooked = 0` in `UClass` payloads. Emits trailing `PACKAGE_FILE_TAG`.
- **SynthPackageWriter:** Procedural package writer constructing summary, name table, import map, export map, depends map, asset registry, payloads, and bulk data from scratch.
- **TaggedPropertyWriter:** Serializes 4.21 tagged properties (Object, Int, Bool, Name, Str, Enum, ByteEnum, GuidStruct, ObjectArray, StaticMaterialsArray, SectionInfoMap).
- **PinSerializer:** Serializes `EdGraphPin` structures with full owning node and deferred `LinkedTo` references.
- **Engine Gate:** Strictly UE 4.21.

### 16. Which classes are editor-loadable?
- **UE 4.21 only:**
  - Whitelisted pure-data assets: `DataTable`, `CurveFloat`, `CurveVector`, `CurveLinearColor`, `CurveTable`, `StringTable`, `UserDefinedEnum`, `UserDefinedStruct`, `SubsurfaceProfile`.
  - Simple `BlueprintGeneratedClass` (via `BlueprintGraphBuilder`).
  - `Texture2D` (via `TextureWriter`).
  - `Material` / `MaterialInstanceConstant` (via `MaterialWriter`).
  - `StaticMesh` (via `CloneMesh` with `--cube`).
  - `World` (via `PlaceActorsCore` with `--template` and `--cube`).
- **All other versions / classes:** Fallback to placeholder headers + JSON model sidecars + glTF/PNG exports.

### 17. What tests already exist?
- **Zero automated test projects.** No `tests/` directory existed; no tests were run in CI or build.
- Only an in-code oracle `WriterSelfTest.cs` executed manually via `--validate-write <virtualPath>`.

### 18. What tests are missing?
- Unit tests for CLI parsing, `VersionDetector`, `AesKeyResolver`, path mapping, glob matching, C++ stub generation, `FCrc` hashing, `FArchiveWriter`, `TaggedPropertyWriter`, `PinSerializer`, `KismetWalker`, and writer eligibility.
- Integration tests for container scanning, asset parsing, and model extraction.
- Round-trip validation tests for generated packages.
- Fuzz testing for malformed binary streams.

### 19. What is Windows-specific?
- Hardcoded backslashes in path operations.
- Windows-specific project and build artifacts (`.exe`, `apphost.exe`, `C:\Users\...` references in `obj/`).
- Stub generator instructions referencing Visual Studio rather than cross-platform build tools.
- CLI examples in documentation formatted exclusively for Windows Command Prompt/PowerShell.

### 20. What is preventing Linux/macOS support?
- Previously: Hardcoded `ProjectReference` to `..\CUE4Parse\CUE4Parse\CUE4Parse.csproj`.
- Native library dependencies (SkiaSharp, Blake3, Oodle) need appropriate runtime packages for macOS (`osx-arm64`, `osx-x64`) and Linux (`linux-x64`).
- Case-sensitivity: Path comparisons on Linux require strict case-insensitive file provider handling.
- Absence of a cross-platform GUI.

### 21. What architecture changes are necessary?
1. Split the single monolithic CLI project into distinct modular projects:
   - `UE4Decompiler.Core`: Core domain models, interfaces, container provider, asset parsing, reconstructors, writers.
   - `UE4Decompiler.Cli`: Rich CLI application with commands (`inspect`, `scan`, `recover`, `export`, `graph`, `doctor`, `validate`, `capabilities`, `version`), exit codes, structured logging, and legacy flag compatibility.
   - `UE4Decompiler.Gui`: Modern Avalonia UI cross-platform desktop application (Windows, Linux, macOS) featuring asset browsers, property inspectors, graph viewers, previews, and job queues.
   - `UE4Decompiler.Tests`: Comprehensive automated unit, integration, and regression test suite.
2. Introduce a clean service layer (`IDecompilerService`, `IAssetDiscoveryService`, `IPackageLoader`, `IAssetReconstructor`, `IRecoveryPipeline`, `IMappingProvider`).
3. Add native `.usmap` mapping support.
4. Implement persistent caching and job resumption.
5. Provide structured logging with configurable log sinks.

### 22. What should be implemented first?
1. Deliver this audit document (`docs/architecture/current-state-audit.md`).
2. Establish the solution structure (`Core`, `Cli`, `Gui`, `Tests`) with clean separation of concerns.
3. Build the Core service abstractions and shared recovery pipeline.
4. Upgrade the CLI with subcommands, exit codes, and 100% backward compatibility for existing flags.
5. Implement `.usmap` mapping support and container inspection (`inspect`, `doctor`).
6. Implement comprehensive unit, integration, and regression tests.
7. Build the Avalonia cross-platform GUI with asset browsing, previews, and Blueprint graph viewing.

### 23. What should explicitly NOT be attempted yet?
- **Do not invent UE5 uncooked package writing.** UE5 package serialization differs fundamentally from UE4.21. For UE5, rely on the verified high-fidelity JSON IR, glTF meshes, PNG textures, and the editor commandlet pipeline.
- **Do not attempt HLSL shader decompilation.** Cooked packages strip HLSL source code.
- **Do not promise lossless Blueprint bytecode round-trips.** Blueprint recovery must remain transparently categorized as partial/stub recovery with full opcode ASTs preserved in JSON IR.

---

## Conclusion

The existing UE4Decompiler codebase possesses substantial reverse-engineering achievements—notably the 4.21 uncooked package writer, Kismet bytecode walker, and procedural asset synthesis. By separating core parsing from application layers, adding `.usmap` mapping support, introducing automated tests, building an Avalonia GUI, and providing a modern CLI, UE4Decompiler can reach the maturity, reliability, and polish of tools like Unity Asset Ripper.
