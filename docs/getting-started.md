# Getting Started with UE4Decompiler

Welcome to **UE4Decompiler**, the cross-platform Unreal Engine 4 and 5 asset recovery and decompilation pipeline for Windows, Linux, and macOS.

UE4Decompiler restores game content from packaged `.pak` files, IoStore `.utoc/.ucas` containers, and loose cooked packages into clean, editable Unreal Engine projects and standard digital content assets (glTF, PNG, WAV, JSON IR).

---

## 1. System Requirements

- **Operating System**:
  - Windows 10 / 11 (x64, arm64)
  - Linux (Ubuntu 20.04+, Debian 11+, Fedora 36+, Arch Linux)
  - macOS 12 Monterey or newer (Apple Silicon & Intel)
- **Runtime**:
  - .NET 8.0 SDK or .NET 8.0 Desktop Runtime

---

## 2. Installation & Quick Build

Clone the repository and build using the provided .NET 8 SDK:

```bash
git clone https://github.com/alphasayshello/UE4Decompiler.git
cd UE4Decompiler

# Build entire solution
dotnet build UE4Decompiler.sln -c Release

# Run the automated test suite
dotnet test tests/UE4Decompiler.Tests/UE4Decompiler.Tests.csproj
```

---

## 3. Running System Diagnostics (`doctor`)

Before processing game containers, run the built-in diagnostic tool to verify environment compatibility:

```bash
dotnet run --project src/UE4Decompiler.Cli/UE4Decompiler.Cli.csproj -- doctor
```

The doctor tool checks:
- .NET runtime version and OS architecture.
- SkiaSharp native image transcoding libraries.
- Disk access and container integrity.
- Engine version support matrix.

---

## 4. Quick Workflow Examples

### A. Inspect a Packaged Game Container
```bash
ue4decompiler inspect ./Game/Content/Paks/Game-WindowsNoEditor.pak
```

### B. Full Uncooked Project Recovery
```bash
ue4decompiler recover ./Game/Content/Paks \
  --output ./RecoveredProject \
  --engine 4.27 \
  --aes-key 0x1234567890ABCDEF...
```

### C. Direct Asset Media Export (glTF / PNG)
```bash
ue4decompiler export ./Game/Content/Paks \
  --output ./ExportedAssets \
  --textures --meshes
```

### D. Blueprint Kismet Graph Decompilation
```bash
ue4decompiler graph ./Game/Content/Paks /Game/Characters/BP_Hero --format mermaid
```

### E. Launch the Avalonia Desktop UI
```bash
dotnet run --project src/UE4Decompiler.Gui/UE4Decompiler.Gui.csproj
```
