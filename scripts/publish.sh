#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

RID="${1:-osx-arm64}"
CONFIG="${2:-Release}"
OUT_DIR="${3:-$ROOT_DIR/dist/$RID}"

echo "==> Publishing UE4Decompiler.Cli for $RID ($CONFIG)..."
dotnet publish "$ROOT_DIR/src/UE4Decompiler.Cli/UE4Decompiler.Cli.csproj" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT_DIR/cli"

echo "==> Publishing UE4Decompiler.Gui for $RID ($CONFIG)..."
dotnet publish "$ROOT_DIR/src/UE4Decompiler.Gui/UE4Decompiler.Gui.csproj" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained true \
    -o "$OUT_DIR/gui"

echo "==> Published successfully to $OUT_DIR"
