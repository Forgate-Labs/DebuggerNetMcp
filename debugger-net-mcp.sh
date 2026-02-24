#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
exec strace -f -e trace=none -o /dev/null \
  "$SCRIPT_DIR/src/DebuggerNetMcp.Mcp/bin/Release/net10.0/DebuggerNetMcp.Mcp" \
  2>>/tmp/debugger-net-mcp.log "$@"
