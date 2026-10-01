# Troubleshooting & Frequently Asked Questions

This guide addresses common questions, operational errors, and troubleshooting steps.

---

## Common Issues & Solutions

### 1. "No containers mounted — they are likely AES-encrypted"
- **Cause**: The `.pak` or `.utoc` archive is encrypted with AES-256 and no valid key was supplied.
- **Solution**:
  - Run `ue4decompiler inspect <file>` to verify encryption status.
  - Supply the 64-character hex key via `--aes-key 0x...`.

---

### 2. "All properties show as unversioned or empty"
- **Cause**: The game was cooked with unversioned property serialization (common in UE 4.26+ and UE 5.x).
- **Solution**:
  - Supply a `.usmap` mapping file using `--mapping <file.usmap>`.

---

### 3. "SkiaSharp Native Library Error on Linux"
- **Cause**: On minimalist Linux distributions, native font and rendering dependencies may be missing.
- **Solution**:
  - Install fontconfig and freetype packages:
    ```bash
    # Ubuntu / Debian:
    sudo apt-get install -y libfontconfig1 libfreetype6

    # Fedora / RHEL:
    sudo dnf install -y fontconfig freetype
    ```
  - Run `ue4decompiler doctor` to verify native graphics support.

---

### 4. "Unreal Editor prompts to build project from source"
- **Cause**: UE4Decompiler generated C++ stub modules (`Source/`) for native classes referenced by Blueprints.
- **Solution**:
  - Right-click the `.uproject` file and select **Generate Visual Studio project files** (or Xcode project on macOS).
  - Open the generated `.sln` or `.xcworkspace` in your IDE and build for **Development Editor**.
  - Launch the project directly from your IDE or the Unreal Editor.

---

### 5. "Blueprint Editor crashes when opening complex Blueprints"
- **Cause**: Cooked Blueprints contain internal VM bytecode and stripped editor metadata.
- **Solution**:
  - Do not use `--dangerously-dump-bpgraph` for production projects.
  - Use clean Blueprint stubs (the default), and reference the decompiled pseudo-code (`ue4decompiler graph`) to re-create custom logic graphs.

---

## Verifying Health with `doctor`

Whenever in doubt, run the diagnostic suite:

```bash
ue4decompiler doctor
```

The output will identify runtime deficiencies, file permission restrictions, and recommended fixes.
