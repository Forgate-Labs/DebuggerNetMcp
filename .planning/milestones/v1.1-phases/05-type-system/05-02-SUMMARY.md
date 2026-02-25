---
phase: "05"
plan: "02"
subsystem: debug-engine
tags: [static-fields, icordebug, variable-reader, pdb-reader, evaluate]
dependency_graph:
  requires: []
  provides: [static-field-reading, dot-notation-eval]
  affects: [GetLocalsAsync, EvaluateAsync, VariableReader, PdbReader]
tech_stack:
  added: []
  patterns: [ICorDebugClass.GetStaticFieldValue, PE-metadata-static-scan, dot-notation-expression]
key_files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Interop/ICorDebug.cs
    - src/DebuggerNetMcp.Core/Engine/VariableReader.cs
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Core/Engine/PdbReader.cs
decisions:
  - "Pass nullable ICorDebugFrame? to GetStaticFieldValue — allows non-thread-static fields to work without frame context"
  - "Static field scan in GetLocalsAsync is best-effort (wrapped in try/catch) — never breaks existing local variable listing"
  - "EvaluateAsync dot-notation lookup is highest priority — runs before state-machine and IL-local paths"
requirements_completed:
  - TYPE-04
metrics:
  duration_seconds: 179
  completed_date: "2026-02-23"
  tasks_completed: 2
  files_changed: 4
---

# Phase 5 Plan 02: Static Field Reading Summary

Static field reading capability added to the debug engine: ICorDebugClass extended with GetStaticFieldValue, VariableReader and PdbReader gained helpers, and both GetLocalsAsync and EvaluateAsync now surface static fields.

## Objective

Enable `debug_variables` and `debug_evaluate` to surface static fields (e.g., `Config.MaxRetries`, `AppSettings.Version`) that were previously invisible because they are not IL locals or instance fields.

## What Was Built

**ICorDebug.cs — ICorDebugClass extended:**
- Added `GetStaticFieldValue(uint fieldDef, ICorDebugFrame? pFrame, out ICorDebugValue ppValue)` as the third vtable method on `ICorDebugClass`, matching the cordebug.idl vtable order.

**VariableReader.cs — two new internal static methods:**
- `ReadStaticFieldsFromPE(string dllPath, uint typedefToken) → Dictionary<uint, string>`: scans PE metadata for static fields, skips `value__` and `<>...` compiler-generated names.
- `ReadStaticField(string name, ICorDebugClass cls, uint fieldToken, ICorDebugFrame? frame) → VariableInfo`: reads via `GetStaticFieldValue`, returns `<not available>` on failure.

**PdbReader.cs — two new public static methods:**
- `GetDeclaringTypeToken(string dllPath, int methodToken) → uint`: returns the TypeDef token of the type declaring a given method.
- `FindTypeByName(string dllPath, string typeName) → uint`: searches all TypeDefinitions for a type by simple name.

**DotnetDebugger.cs — GetLocalsAsync extended:**
- After existing local enumeration (state machine or IL locals), a best-effort static field scan appends static fields from the current method's declaring type.

**DotnetDebugger.cs — EvaluateAsync extended:**
- Highest-priority path: if expression contains `.`, try "TypeName.FieldName" static field lookup using `FindTypeByName` + `ReadStaticFieldsFromPE` + `ReadStaticField`.
- Falls through to state-machine and IL-local paths if static lookup finds nothing.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Variable name conflict `fn` in GetLocalsAsync static scan**
- **Found during:** Task 2 build
- **Issue:** The foreach loop variable `fn` conflicted with the outer scope `ICorDebugFunction fn` already declared in the same DispatchAsync lambda.
- **Fix:** Renamed loop variable to `sfn` (static field name).
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Commit:** 205d298

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | f66f467 | feat(05-02): extend ICorDebugClass + add VariableReader static field helpers |
| 2 | 205d298 | feat(05-02): extend GetLocalsAsync and EvaluateAsync for static field access |

## Self-Check: PASSED

All source files exist. Both task commits verified in git history. Build passes with 0 errors.
