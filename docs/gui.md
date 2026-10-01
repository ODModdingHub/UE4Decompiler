# Desktop Graphical User Interface (GUI) Guide

**UE4Decompiler GUI** provides a cross-platform desktop experience built with Avalonia UI and CommunityToolkit.Mvvm, designed with the usability and polish expected from mature recovery tools like Unity Asset Ripper.

---

## Launching the GUI

```bash
dotnet run --project src/UE4Decompiler.Gui/UE4Decompiler.Gui.csproj
```

---

## UI Views & Workflow

### 1. Home Screen
- **Input & Output Configuration**: Configure source containers (`.pak`, `.utoc`, or game directories) and target output project directories.
- **Engine Version Selector**: Select target engine version or allow auto-detection.
- **AES-256 Key Input**: Enter encryption keys for encrypted game archives.
- **Container Scanner**: Lists discovered containers, mount points, and asset counts before extraction.
- **Start Recovery**: Initiates the full decompile pipeline.

### 2. Asset Browser
- **Live Search & Filter**: Search assets in real-time by name or virtual path.
- **Category Filters**: Filter assets by class (`Blueprint`, `StaticMesh`, `SkeletalMesh`, `Texture2D`, `Material`, `World`, `SoundWave`).
- **Asset Metadata Inspector**: Inspect package size, mount point, extension, and recovery strategy.

### 3. Recovery Queue
- **Live Progress Monitor**: Track progress percentage, elapsed time, current pipeline stage, and currently processed asset.
- **Cancellation**: Safely cancel recovery operations in progress.
- **Execution Log**: Live scrolling log stream showing operations and warnings.

### 4. Doctor Diagnostics
- **One-Click Health Check**: Evaluates runtime environment, native SkiaSharp rendering libraries, and file permissions.
- **Actionable Fixes**: Provides suggested remediation steps for any discovered warnings or failures.

### 5. Settings
- **Concurrency Slider**: Configure thread count for optimal multi-core performance.
- **Persistent Disk Cache**: Enable or disable SHA256 disk caching for incremental extraction.
- **Theme Preferences**: Switch between Dark and Light Fluent UI themes.
