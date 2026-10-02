# Complete Recovery Pipeline & Workflow

This document details the end-to-end recovery pipeline executed by UE4Decompiler when transforming a packaged game into a clean, editor-openable Unreal project.

---

## The Recovery Pipeline Lifecycle

```
[Game Containers (.pak / .utoc)]
              │
              ▼
   1. Container Discovery & AES Decryption
              │
              ▼
   2. Engine & Schema Resolution (.usmap)
              │
              ▼
   3. Project Scaffolding (.uproject, Config/, Plugins/)
              │
              ▼
   4. Parallel Asset Recovery & Reconstructor Routing
      ├── Textures     ──> PNG + Editor Uncooked Texture
      ├── Meshes       ──> glTF 2.0 (.glb) + FRawMesh
      ├── Materials    ──> JSON Parameter Tree + Template
      ├── Blueprints   ──> Pseudo-Code + Clean Stub
      └── Levels       ──> Actor Placement & Transforms
              │
              ▼
   5. C++ Game Native Stub Module Generation
              │
              ▼
   6. Manifest & Validation Reporting (decompile-report.json)
```

---

## 1. Project Scaffolding
Before parsing assets, `ProjectScaffold` creates a clean directory layout:
- `ProjectName.uproject`: Target engine association, default plugins enabled.
- `Config/DefaultEngine.ini`: Mount points, render settings, and default game mode.
- `Content/`: Mirrored virtual path structure for game assets.
- `Plugins/`: Content-only `.uplugin` mounts for every detected plugin in the archive.
- `Source/`: C++ stub modules for referenced native game classes.

---

## 2. Reconstructor Routing & Asset Processing
Assets are processed concurrently across worker threads:
- **`TextureReconstructor`**: Extracts largest mip matching dimension limits and transcode to PNG.
- **`MeshReconstructor`**: Converts geometry into glTF 2.0 format (`.glb`).
- **`MaterialReconstructor`**: Walks material input expressions and records parameter assignments.
- **`BlueprintReconstructor`**: Extracts component hierarchy, parent class, and disassembles Kismet bytecode.
- **`LevelReconstructor`**: Maps placed actors and their components.

---

## 3. C++ Stub Module Generation
When assets subclass native game classes (e.g. `AMyCustomCharacter`), opening them without the corresponding C++ class triggers editor errors. UE4Decompiler parses the import table, identifies game-specific classes, and generates:
- `<ModuleName>.Build.cs`
- `<ClassName>.h` with proper `UCLASS()`, `GENERATED_BODY()`, and parent inheritance.
- `<ClassName>.cpp` with empty method implementations.

This allows the project to build cleanly in Visual Studio / Xcode / Rider and launch in the Unreal Editor.
