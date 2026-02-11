# DebuggerNetMcp

MCP server that enables interactive debugging of .NET Core projects via the Debug Adapter Protocol (DAP).

**Architecture:** `Claude Code → MCP Server (stdio) → DAP Client (async) → netcoredbg → .NET Process`

## Features

- Launch and debug .NET projects with breakpoints
- Attach to running .NET processes
- Step through code (over, into, out)
- Inspect variables and evaluate C# expressions
- View call stack and program output
- Full async implementation using asyncio

## Prerequisites

- Python 3.11+
- .NET SDK 8.0
- netcoredbg (compiled from source recommended)

## Building netcoredbg from source

The pre-built netcoredbg binaries may SIGSEGV on `configurationDone`. Building from source resolves this.

```bash
# Install build dependencies
sudo apt install -y clang cmake git libicu-dev

# Install .NET SDK 8.0 (if not already installed)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$DOTNET_ROOT:$PATH

# Clone and build netcoredbg
git clone --depth 1 https://github.com/Samsung/netcoredbg.git /tmp/netcoredbg-src
cd /tmp/netcoredbg-src
mkdir build && cd build
CC=clang CXX=clang++ cmake .. -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$HOME/.local
make -j$(nproc)
make install

# Verify installation
# Files will be in ~/.local/ (netcoredbg, ManagedPart.dll, libdbgshim.so, etc.)
# Copy all files to ~/.local/bin/ so they're co-located:
cp $HOME/.local/netcoredbg $HOME/.local/bin/
cp $HOME/.local/ManagedPart.dll $HOME/.local/Microsoft.CodeAnalysis*.dll $HOME/.local/libdbgshim.so $HOME/.local/bin/

# Verify
$HOME/.local/bin/netcoredbg --version
```

## Installation

```bash
python3 -m venv .venv
source .venv/bin/activate
pip install -e .
```

## DAP Protocol Order

The correct DAP initialization sequence (verified via testing):

```
initialize → launch(stopAtEntry) → wait "initialized" event → setBreakpoints → configurationDone → wait "stopped" event
```

This follows the standard DAP specification (Order A). Both Order A and the alternative "netcoredbg-specific" order (where launch comes after configurationDone) work with source-compiled netcoredbg.

## Registering with Claude Code

Add to `~/.claude/settings.json`:

```json
{
  "mcpServers": {
    "debugger-net": {
      "command": "python3",
      "args": ["-m", "debugger_net_mcp"],
      "env": {
        "PYTHONPATH": "/path/to/DebuggerNetMcp/src",
        "DOTNET_ROOT": "/path/to/.dotnet",
        "NETCOREDBG_PATH": "/path/to/.local/bin/netcoredbg",
        "LD_LIBRARY_PATH": "/path/to/.local/bin"
      }
    }
  }
}
```

Replace `/path/to/` with your actual paths (e.g., `$HOME`).

## Usage example

Once registered, Claude Code can use the debug tools:

```
> debug_launch("/path/to/MyProject")   # Build & launch with debugger
> debug_set_breakpoint("Program.cs", 10)  # Set breakpoint
> debug_continue()                        # Run to breakpoint
> debug_variables()                       # Inspect variables
> debug_evaluate("myVar.ToString()")      # Evaluate expressions
> debug_stacktrace()                      # View call stack
> debug_step_over()                       # Step to next line
> debug_disconnect()                      # End debug session
```

## Running tests

```bash
# Create a test project
mkdir -p /tmp/debug-test
dotnet new console -n HelloDebug -o /tmp/debug-test/HelloDebug --framework net8.0
# Edit Program.cs with test code, then:
dotnet build /tmp/debug-test/HelloDebug -c Debug

# Run raw DAP protocol test
python3 test_dap_protocol.py

# Run integration test (uses Python session classes)
python3 test_integration.py
```

## License

MIT
