# UE4Decompiler

A .NET 8 console tool that takes **cooked** Unreal Engine 4/5 game files and produces an
openable `.uproject` with recovered, uncooked-style assets. Built around
[CUE4Parse](https://github.com/FabianFG/CUE4Parse) as the core parsing library.

> Intended for modding, asset research, education, and recovering your own projects. Respect the
> IP/EULA of any content you process.

## Build

The local CUE4Parse source must sit next to this project (it does in this repo):

```
CUE4ProjectGenerator/
├── CUE4Parse/            ← cloned CUE4Parse solution
└── UE4Decompiler/        ← this project
```

```sh
cd UE4Decompiler
dotnet build -c Release
```

## Usage

```sh
UE4Decompiler.exe \
  --input   "C:/Game/Content/Paks/pakchunk0-WindowsNoEditor.pak" \
  --output  "C:/Output/MyProject" \
  --version "4.27"            # optional, auto-detected if omitted
  --aes-key "0xABCD..."       # optional, omit if unencrypted
  --filter  "Characters/**"   # optional glob over virtual paths
  --skip-blueprints           # optional: blueprint metadata stubs only
  --full-recovery             # optional: emit Kismet bytecode + anim-graph order for the re-import commandlet
  --dry-run                   # optional: list, don't write
  --report                    # optional: emit decompile-report.json
  --verbose
```

## What it produces

```
MyProject/
├── MyProject.uproject
├── Config/{DefaultEngine,DefaultGame,DefaultEditor}.ini
├── decompile-report.json          # with --report
└── Content/<mirrored pak tree>/
    ├── Foo.uasset                 # structurally-valid uncooked header (placeholder)
    ├── Foo.json                   # full recovered model (source of truth)
    ├── Foo.png                    # textures decoded to source PNG
    └── Bar.glb                    # meshes exported as glTF 2.0
```

## Reconstruction fidelity

| Asset type            | Fidelity | How |
|-----------------------|----------|-----|
| Material / MIC        | **Full** | Expression graph (nodes, pins, params) survives cooking and is parsed directly |
| Texture2D / Cube      | **Full** | Largest mip decoded (BC1-7/ASTC/etc.) → source PNG; SRGB/compression/filter preserved |
| Static / Skeletal Mesh| Partial  | LOD0 geometry (verts/indices/UVs/normals, bones, morphs) → glTF 2.0 for re-import |
| World / Level (.umap) | Partial  | Actors, components, transforms placed; streaming refs kept as soft paths |
| Blueprint             | Stub     | Kismet bytecode pattern-matched to K2 nodes; unmapped opcodes become commented stubs |
| SoundWave             | Copied   | Model preserved; raw PCM/OGG bulk left as-is |
| Other                 | Partial  | Full property model preserved as JSON |
| Parse failure         | Failed   | Placeholder header + metadata so the project still opens |

## Editor-loadable uncooked writer

For UE 4.21 targets, the tool emits **genuine editor-loadable uncooked `.uasset` files** via a
hand-rolled package serializer ([Output/Writer/](Output/Writer/)) grounded in the engine source:

- Rebuilds the package framework (summary + name/import/export tables) for the **single-file**
  uncooked layout, with correct `FNameEntrySerialized` hashes (`FCrc` port).
- Reuses the verbatim name table and **copies the real tagged-property payloads** (the format is
  identical cooked↔uncooked), relocating them out of the split `.uexp`, and **clears the cooked
  flags** (`PKG_Cooked` / `PKG_FilterEditorOnly`).
- Preserves the source's versioning + custom-version container so payloads deserialize identically.
- Field order is generated from the same version gates CUE4Parse's reader uses, so writer↔reader
  can't drift across engine versions.

Validated by round-tripping output back through CUE4Parse (`--validate-write "<ObjectPath>"`):
property-only assets and a 17-export blueprint (BlueprintGeneratedClass + CDO + functions +
components) re-parse with matching classes and properties.

**Eligibility is a verified whitelist** (audit finding [9.1]): only classes whose editor-mode
`Serialize` consumes exactly the cooked `SerialSize` — pure tagged-property classes (curves,
data/material/particle assets) plus the verified `UStruct`/`UClass`/BlueprintGeneratedClass family —
are emitted as real packages. Everything else (and any package containing a hard-excluded
bulk/native class: textures, meshes, sounds, **AnimSequence**, fonts, …) **falls back to a
placeholder header**, because a class with `!IsFilterEditorOnly()` native serialization or `.ubulk`
payloads would trip the loader's `Fatal` `SerialSize` mismatch. The `.json` + PNG/glb sidecars
remain the recovery path for excluded types. Cooked-class payloads additionally have their
`bCooked` flag patched to `false` so the editor treats imported blueprint classes as uncooked.

## Other limitations (by design)
- Blueprint recovery is best-effort: "opens with most logic visible", not a byte-perfect round-trip.
- Shader bytecode → HLSL is **not** attempted; shader caches are out of scope.
- `/Engine/` content is **never** copied — only referenced via soft paths.
- Audio is copied as raw bulk, not transcoded.

## Full-recovery JSON contract (`--full-recovery`)

For editor-loadable reconstruction, a companion UE editor commandlet (`JsonAssetImport`) consumes
the JSON sidecars. `--full-recovery` enriches Blueprint/AnimBlueprint sidecars with:

- `SuperStruct` — parent class path. For game-native parents (e.g. `/Script/VRFramework.VRGunAnimInstance`)
  the commandlet reparents to the base `UAnimInstance` (the game C++ module isn't in a stock engine).
- `TargetSkeleton` — resolved skeleton package path (used by `UAnimBlueprintFactory`).
- `IsAnimBlueprint`, `AnimGraphNodeOrder` — export order, used to resolve anim-graph `LinkID` → node.
- `Functions[].Bytecode` — normalized Kismet opcode tree per function:

  ```json
  { "Opcode": "EX_FinalFunction", "Offset": 0,
    "FunctionRef": "/Script/Engine.KismetMathLibrary:Multiply_FloatFloat",
    "Parameters": [ { "Opcode": "EX_LocalVariable", "Offset": 9,
                      "OperandName": "TriggerRatio", "OperandType": "FloatProperty" } ] }
  ```

  Keys: `Opcode` (EExprToken), `Offset` (statement index), `OperandName`/`OperandType` (variable refs),
  `FunctionRef` (resolved UFunction path), and nested expressions under their field name
  (`Parameters`, `ObjectExpression`, `ReturnExpression`, …). Requires `provider.ReadScriptData=true`,
  which `PakExtractor` enables automatically when blueprints are in scope.

> Note: cooked shipping packages are **unversioned**, so the engine version cannot be auto-detected
> from the header (it reads back as the mount default). Always pass `--version` for these.

## Architecture

```
Program.cs            CLI (CommandLineParser) + pipeline + Spectre progress + summary
Core/
  PakExtractor        DefaultFileProvider mount, AES, glob enumeration
  AssetParser         LoadPackage + export/import walk → ParsedAsset
  AssetWriter         JSON model + FPackageFileSummary header writer
Reconstructors/       Texture, Mesh, Material, Blueprint, Level (one class each)
Output/
  ProjectScaffold     extracts the REAL .uproject + Config/*.ini from the pak (patches
                      .uproject for stock-editor opening: EngineAssociation->version, strips
                      native modules, drops project plugins); generates stand-ins only if absent
  ContentWriter       path mapping, reconstructor routing, manifest
Utils/
  VersionDetector     hint parse + package-version sniffing
  AesKeyResolver      hex key + executable entropy-scan hook
```
