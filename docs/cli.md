# Command-Line Interface (CLI) Reference

The `ue4decompiler` CLI provides high-performance, scriptable commands for Unreal Engine container discovery, asset recovery, validation, and diagnostics.

---

## Command Syntax

```bash
ue4decompiler <command> [options]
```

### Global Options

| Option | Description |
| :--- | :--- |
| `--verbose`, `-v` | Enables verbose debug logging |
| `--quiet` | Suppresses non-essential console output |
| `--json` | Formats output as structured JSON for automation |
| `--log-file <path>` | Writes diagnostic execution logs to a file |
| `--no-color` | Disables ANSI color escapes |

---

## Subcommands

### 1. `inspect`
Inspects container metadata, mount points, encryption, and asset counts without modifying files.

```bash
ue4decompiler inspect <input-path> [--engine <ver>] [--aes-key <hex>] [--mapping <usmap>] [--json]
```

### 2. `scan`
Recursively scans a directory for Unreal Engine containers and enumerates all virtual package paths.

```bash
ue4decompiler scan <directory> [--filter <glob>] [--engine <ver>] [--aes-key <hex>] [--json]
```

### 3. `recover`
Executes the full asset recovery pipeline, producing an uncooked Unreal Engine project with `.uproject`, `Config/`, `Content/`, `Plugins/`, and C++ native stub modules.

```bash
ue4decompiler recover <input> --output <dir> [options]
```

#### Key Options:
- `--engine <ver>`: Unreal Engine version hint (e.g. `4.21`, `4.27`, `5.1`, `5.3`).
- `--aes-key <hex>`: AES-256 key as 64 hex characters (with or without `0x`).
- `--mapping <usmap>`: Path to `.usmap` unversioned property mapping file.
- `--filter <glob>`: Filter assets by virtual path glob, e.g. `"Characters/**"`.
- `--no-media`: Skips CPU-intensive mesh/texture transcoding; writes `.uasset` + JSON IR only.
- `--skip-blueprints`: Emits clean Blueprint stubs instead of attempting graph reconstruction.
- `--full-recovery`: Decompiles complete Kismet bytecode and anim-graph ordering into JSON IR.
- `--emit-stubs`: Generates compilable C++ modules (`.Build.cs`, `.h`, `.cpp`) for referenced native game classes.
- `--threads <n>`: Concurrency worker threads (0 = auto-detect CPU cores).
- `--dry-run`: Simulates the recovery plan without writing files to disk.

### 4. `export`
Exports media assets directly to industry-standard interchange formats.

```bash
ue4decompiler export <input> --output <dir> [--textures] [--meshes] [--materials] [--filter <glob>]
```
- Meshes exported as glTF 2.0 (`.glb`) with vertex attributes, bone weights, and morph targets.
- Textures exported as PNG.
- Materials exported as JSON expression graphs and HLSL shaders.

### 5. `graph`
Decompiles a cooked Blueprint's Kismet bytecode into visualization formats or pseudo-code.

```bash
ue4decompiler graph <input> <asset-virtual-path> [--format pseudo|dot|mermaid] [-o <file>]
```

### 6. `validate`
Validates recovered assets on disk, verifying package summaries, export tables, and JSON models.

```bash
ue4decompiler validate <recovered-directory> [--engine <ver>] [--json]
```

### 7. `doctor`
Runs comprehensive system and environment health checks.

```bash
ue4decompiler doctor [--input <container>] [--output <dir>] [--json]
```

### 8. `capabilities`
Prints the engine version, container format, and asset class recovery capability matrix.

```bash
ue4decompiler capabilities [--json]
```

### 9. `version`
Displays version and runtime information.

```bash
ue4decompiler version [--json]
```

---

## Exit Codes

| Code | Name | Description |
| :---: | :--- | :--- |
| `0` | Success | Operation completed successfully |
| `1` | General Failure | Unhandled fatal exception or error |
| `2` | Invalid Arguments | Missing required parameters or conflicting flags |
| `3` | Unsupported Input | Specified file or directory was not found or unsupported |
| `4` | Authentication Failure | AES-256 key missing or incorrect for encrypted container |
| `5` | Parse Failure | Package parsing failed |
| `6` | Output Failure | Disk write error or permission denied |
| `7` | Partial Recovery | Recovery completed with some assets falling back to placeholders |
| `8` | Validation Failure | One or more recovered packages failed validation checks |
