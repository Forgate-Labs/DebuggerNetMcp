---
phase: 04-mcp-server
plan: 01
subsystem: mcp
tags: [modelcontextprotocol, mcp, stdio, di, dotnet, hosting]

# Dependency graph
requires:
  - phase: 03-debug-engine
    provides: DotnetDebugger singleton — the core debug engine wired as DI service
provides:
  - ModelContextProtocol 0.9.0-preview.2 added to DebuggerNetMcp.Mcp csproj
  - Program.cs MCP stdio host with DotnetDebugger DI singleton and stderr log routing
  - HelloDebug TEST-01 confirmation (9 debug sections, DebugType=portable, Optimize=false)
affects: [04-02-PLAN.md, DebuggerTools class hosting context]

# Tech tracking
tech-stack:
  added: [ModelContextProtocol 0.9.0-preview.2, ModelContextProtocol.Core 0.9.0-preview.2]
  patterns: [MCP stdio transport via AddMcpServer().WithStdioServerTransport(), stderr-only logging to preserve stdout for MCP wire protocol, DotnetDebugger as IHost singleton]

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj
    - src/DebuggerNetMcp.Mcp/Program.cs

key-decisions:
  - "Logging routed to stderr (LogToStandardErrorThreshold=Trace) — stdout must be clean for MCP wire protocol"
  - "DebuggerTools forward-declared in WithTools<DebuggerTools>() — type satisfied by Plan 02, project intentionally unbuildable until then"

patterns-established:
  - "MCP host pattern: Host.CreateApplicationBuilder + AddMcpServer().WithStdioServerTransport().WithTools<T>()"
  - "Stderr log routing pattern: AddConsole with LogToStandardErrorThreshold=LogLevel.Trace"

requirements-completed: [MCP-01, TEST-01]

# Metrics
duration: 1min
completed: 2026-02-23
---

# Phase 4 Plan 01: MCP Server Entry Point Summary

**ModelContextProtocol 0.9.0-preview.2 wired to DebuggerNetMcp.Mcp via stdio transport with DotnetDebugger singleton and stderr-only logging; HelloDebug TEST-01 confirmed (9 sections, portable PDB, no optimizations)**

## Performance

- **Duration:** 1 min
- **Started:** 2026-02-23T00:20:03Z
- **Completed:** 2026-02-23T00:21:15Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Added ModelContextProtocol 0.9.0-preview.2 NuGet package to DebuggerNetMcp.Mcp.csproj; dotnet restore exits 0 cleanly
- Replaced placeholder Program.cs with real MCP stdio host: AddMcpServer/WithStdioServerTransport/WithTools<DebuggerTools>/AddSingleton<DotnetDebugger> with stderr log routing
- Confirmed HelloDebug satisfies TEST-01 without any code changes: 9 debug sections, DebugType=portable, Optimize=false, builds clean

## Task Commits

Each task was committed atomically:

1. **Task 1: Add ModelContextProtocol package and write Program.cs** - `a9e37f5` (feat)
2. **Task 2: Verify HelloDebug satisfies TEST-01** - no-op verification, no files changed

**Plan metadata:** (docs commit below)

## Files Created/Modified
- `src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj` - Added ModelContextProtocol 0.9.0-preview.2 PackageReference
- `src/DebuggerNetMcp.Mcp/Program.cs` - Full MCP host replacing "Hello, World!" placeholder

## Decisions Made
- Logging routed to stderr via `LogToStandardErrorThreshold = LogLevel.Trace` — stdout must be reserved exclusively for the MCP wire protocol JSON-RPC messages
- `DebuggerTools` forward-declared in `WithTools<DebuggerTools>()` — the type does not exist yet (Plan 02 creates it); project is intentionally unbuildable until Plan 02 completes

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The plan's `grep -c "Section [1-9]"` check returns 0 because the file uses `SECTION` (uppercase); `grep -c "SECTION"` correctly returns 9. This is a documentation discrepancy in the plan, not an issue with the code. TEST-01 is confirmed satisfied.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Mcp project entry point is wired and restores cleanly
- Plan 02 can now add DebuggerTools.cs to satisfy the forward reference and make the project buildable
- HelloDebug is ready to serve as the test binary for all debugger verification tasks

---
*Phase: 04-mcp-server*
*Completed: 2026-02-23*
