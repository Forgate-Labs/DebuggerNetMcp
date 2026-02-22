#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLAUDE_BIN="${CLAUDE_BIN:-${HOME}/.local/bin/claude}"

# Verify claude binary exists
if ! command -v "$CLAUDE_BIN" &>/dev/null; then
    echo "ERROR: claude binary not found at $CLAUDE_BIN"
    echo "Set CLAUDE_BIN environment variable to the correct path."
    exit 1
fi

echo "==> Registering debugger-net MCP server..."

# Remove existing registration (idempotent — ignore error if not registered)
"$CLAUDE_BIN" mcp remove debugger-net -s user 2>/dev/null || true

# Register with user scope, stdio transport
# -e passes environment variables to the spawned dotnet process
# -- separates mcp add options from the command to run
# --no-build prevents dotnet from rebuilding on each MCP invocation (build.sh handles builds)
"$CLAUDE_BIN" mcp add \
    -s user \
    -e DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}" \
    -e LD_LIBRARY_PATH="$SCRIPT_DIR/lib${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    -- \
    debugger-net \
    dotnet run \
        --project "$SCRIPT_DIR/src/DebuggerNetMcp.Mcp" \
        --no-build \
        -c Release

echo "==> MCP server 'debugger-net' registered at user scope."
echo "    Restart Claude Code to pick up the new registration."
