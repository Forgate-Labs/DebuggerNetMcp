#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Building managed projects (dotnet)..."
dotnet build "$SCRIPT_DIR/DebuggerNetMcp.sln" -c Release

echo ""
echo "==> Build complete."
echo "    Managed: dotnet build -c Release completed"
