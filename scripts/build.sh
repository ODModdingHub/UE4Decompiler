#!/usr/bin/env bash
set -euo pipefail

DOTNET_CMD="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

echo "==> Building UE4Decompiler Solution (Release) using $DOTNET_CMD..."
"$DOTNET_CMD" build UE4Decompiler.sln -c Release

echo "==> Build completed successfully!"
