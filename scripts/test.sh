#!/usr/bin/env bash
set -euo pipefail

DOTNET_CMD="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

echo "==> Running UE4Decompiler Automated Test Suite using $DOTNET_CMD..."
"$DOTNET_CMD" test tests/UE4Decompiler.Tests/UE4Decompiler.Tests.csproj --verbosity normal

echo "==> All tests executed successfully!"
