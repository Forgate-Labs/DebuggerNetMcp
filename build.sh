#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Building native library (CMake)..."
cmake -S "$SCRIPT_DIR/native" -B "$SCRIPT_DIR/native/build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$SCRIPT_DIR/native/build" --parallel

echo "==> Building managed projects (dotnet)..."
dotnet build "$SCRIPT_DIR/DebuggerNetMcp.sln" -c Release

echo ""
echo "==> Build complete."
echo "    Native: $SCRIPT_DIR/lib/libdotnetdbg.so"
echo "    Managed: dotnet build -c Release completed"
