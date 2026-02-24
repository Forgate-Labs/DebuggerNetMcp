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
# The wrapper script handles DOTNET_ROOT, strace workaround, and logging
"$CLAUDE_BIN" mcp add \
    -s user \
    -- \
    debugger-net \
    "$SCRIPT_DIR/debugger-net-mcp.sh"

echo "==> MCP server 'debugger-net' registered at user scope."
echo "    Restart Claude Code to pick up the new registration."
