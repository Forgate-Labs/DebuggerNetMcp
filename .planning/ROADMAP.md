# Roadmap: DebuggerNetMcp

## Milestones

- ✅ **v1.0 C# Rewrite** — Phases 1-4 (shipped 2026-02-22)
- ✅ **v1.1 Complete .NET Debug Coverage** — Phases 5-10 (shipped 2026-02-25)

## Phases

<details>
<summary>✅ v1.0 C# Rewrite (Phases 1-4) — SHIPPED 2026-02-22</summary>

Complete rewrite from Python/netcoredbg to C#/ICorDebug. Eliminates SIGSEGV on kernel 6.12+ at the root.

- [x] Phase 1: Foundation — Delete Python, create C# solution + native ptrace wrapper (3/3 plans)
- [x] Phase 2: Interop + Engine Foundation — COM interfaces, models, PdbReader, VariableReader (3/3 plans)
- [x] Phase 3: Debug Engine — DotnetDebugger.cs complete (5/5 plans)
- [x] Phase 4: MCP Server — 14 tools via ModelContextProtocol SDK (2/2 plans)

See: `.planning/milestones/v1.0-ROADMAP.md` (archived with v1.1)

</details>

<details>
<summary>✅ v1.1 Complete .NET Debug Coverage (Phases 5-10) — SHIPPED 2026-02-25</summary>

Expanded debug engine covering all major .NET patterns. Full type system, object graph inspection, exceptions, multi-threading, process attach, readable stack traces, xUnit test debugging, automated integration tests, and complete documentation.

- [x] Phase 5: Type System — struct, enum, Nullable<T>, static fields (3/3 plans, completed 2026-02-23)
- [x] Phase 6: Closures, Iterators & Object Graph — lambda captures, yield return, circular refs (3/3 plans, completed 2026-02-23)
- [x] Phase 7: Exceptions, Threading & Attach — exception events, multi-thread, debug_attach (3/3 plans, completed 2026-02-23)
- [x] Phase 8: Stack Trace & dotnet test — reverse PDB lookup, xUnit debug (2/2 plans, completed 2026-02-23)
- [x] Phase 9: Tests & Documentation — 14 xUnit integration tests, README rewrite (3/3 plans, completed 2026-02-23)
- [x] Phase 10: Tech Debt Cleanup — portable scripts, CMake removed, THRD-03/DTEST-02 tests (2/2 plans, completed 2026-02-24)

See: `.planning/milestones/v1.1-ROADMAP.md`

</details>

## Progress

| Phase | Milestone | Plans | Status | Completed |
|-------|-----------|-------|--------|-----------|
| 1. Foundation | v1.0 | 3/3 | ✅ Complete | 2026-02-22 |
| 2. Interop + Engine Foundation | v1.0 | 3/3 | ✅ Complete | 2026-02-22 |
| 3. Debug Engine | v1.0 | 5/5 | ✅ Complete | 2026-02-22 |
| 4. MCP Server | v1.0 | 2/2 | ✅ Complete | 2026-02-23 |
| 5. Type System | v1.1 | 3/3 | ✅ Complete | 2026-02-23 |
| 6. Closures, Iterators & Object Graph | v1.1 | 3/3 | ✅ Complete | 2026-02-23 |
| 7. Exceptions, Threading & Attach | v1.1 | 3/3 | ✅ Complete | 2026-02-23 |
| 8. Stack Trace & dotnet test | v1.1 | 2/2 | ✅ Complete | 2026-02-23 |
| 9. Tests & Documentation | v1.1 | 3/3 | ✅ Complete | 2026-02-23 |
| 10. Tech Debt Cleanup | v1.1 | 2/2 | ✅ Complete | 2026-02-24 |
