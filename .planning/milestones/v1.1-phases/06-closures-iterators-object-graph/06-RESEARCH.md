# Phase 6: Closures, Iterators & Object Graph - Research

**Researched:** 2026-02-23
**Domain:** C# compiler-generated state machines (closures, iterators), circular object graph detection, computed property reporting via PE metadata
**Confidence:** HIGH — all key facts verified against live PE metadata from compiled .NET 10 binaries

---

## Summary

Phase 6 extends the debugger to handle three categories of runtime objects that require special treatment beyond the generic field-enumeration path already in `VariableReader.ReadObjectFields`.

**Closures (CLSR-01):** When a lambda captures variables, the C# compiler generates a class named `<>c__DisplayClass{N}_{M}`. The captured variables become instance fields of this class with their **original names** (no mangling — `captured`, `capturedStr`, not `<captured>5__1`). `GetLocalsAsync` currently reads the active IL frame's locals. When stopped inside a lambda, the active frame is a method like `<<Main>$>b__0` on the display class, and `GetArgument(0)` returns `this` — the display class instance. Reading that object's fields with `ReadObjectFields` will work directly if we skip the `<>` infrastructure fields. However, the display class is already a reference type object; the key insight is that `GetLocalsAsync` already handles `MoveNext` state machines via `GetArgument(0)`. We need to extend this pattern to also handle display class methods (named `b__N`).

**Iterators (CLSR-02):** A `yield return` method compiles to a class named `<MethodName>d__N` (e.g., `<<<Main>$>g__GetNumbers|0_1>d`). Its fields are: `<>1__state` (int, execution state), `<>2__current` (the last yielded value, user-visible as `Current`), `<>l__initialThreadId` (infrastructure, skip), and any hoisted locals named `<varName>N__M`. The `MoveNext` method structure is identical to async state machines — the current code already handles `MoveNext`. The key difference from async state machines: the iterator state machine also has `<>2__current` which should be displayed as `Current`.

**Circular References (GRAPH-01):** `VariableReader.ReadValue` calls `ReadObject` which recurses with `depth + 1`, capped at `MaxDepth = 3`. This prevents crashes for shallow graphs, but the `MaxDepth` guard is depth-based, not cycle-aware. For a truly circular graph (A.Self = A), at depth 3 it returns `<max depth>` — not ideal but not a crash. The correct fix is to track visited object addresses in a `HashSet<ulong>` passed through the call chain. `ICorDebugValue.GetAddress(out ulong pAddress)` is already declared in `ICorDebug.cs` and returns the GC heap address. If an address is already in the visited set, return `"<circular reference>"` immediately. Address 0 means "not available" (enregistered value) — treat 0 as non-circular.

**Computed Properties (GRAPH-02):** `ReadObjectFields` uses `ReadInstanceFieldsFromPE` which only returns fields, never properties. Auto-properties have a `<PropName>k__BackingField` field and are already shown. Computed properties (expression-bodied, body-only getters) have no backing field — they are simply absent from the output today. The fix: after reading instance fields, also enumerate `typeDef.GetProperties()` from PE metadata. For each property, check if `<PropName>k__BackingField` exists in the field set. If it does, the field was already shown. If it does not, add a `VariableInfo` with value `"<computed>"`.

**Primary recommendation:** Extend `GetLocalsAsync` display-class detection + update `VariableReader.ReadObjectFields` to add visited-set tracking + computed property reporting. No new ICorDebug interfaces needed; no new PDB reader methods needed for CLSR-01/CLSR-02.

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| CLSR-01 | Debugger displays closure-captured variables with clean names | Display class fields use original names; `GetArgument(0)` on `b__N` frame gives closure instance |
| CLSR-02 | Debugger inspects iterator state — Current value and internal state | `<>2__current` = Current, `<>1__state` = state; existing MoveNext handling extends to iterators |
| GRAPH-01 | VariableReader detects circular references, returns marker instead of crashing | `GetAddress()` returns `ulong` heap pointer; `HashSet<ulong>` visited set threaded through `ReadValue` |
| GRAPH-02 | Computed properties reported as `<computed>` rather than absent | `typeDef.GetProperties()` + check for `<PropName>k__BackingField` field; no field = computed |
| TEST-08 | HelloDebug sections 13-19 covering struct, enum, nullable, closure, iterator, threading, circular ref | Sections 13-16 already exist; add sections 17 (closure), 18 (iterator), 19 (circular ref) |
</phase_requirements>

---

## Standard Stack

### Core (already in use — no new dependencies)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Reflection.Metadata | .NET 10 BCL | PE metadata: TypeDefinition.GetProperties(), field enumeration | Already used throughout VariableReader/PdbReader |
| System.Reflection.PortableExecutable | .NET 10 BCL | PE file reading | Already used |
| ICorDebug COM interfaces | libdbgshim 10.0.14 | `GetAddress()` on `ICorDebugValue` | Already declared in ICorDebug.cs line 260 |

### No New Packages Required
All implementation uses existing dependencies. The phase is purely an extension of existing patterns.

---

## Architecture Patterns

### Pattern 1: Closure Detection in GetLocalsAsync

**What:** When stopped inside a lambda (`b__N` method on a `<>c__DisplayClass` type), treat `GetArgument(0)` (the `this` pointer) as the source of variables, just like async state machines.

**When to use:** When `smMethodName` matches the pattern `b__N` (contains `>b__`) AND the declaring type name contains `<>c__DisplayClass`.

**Current code flow (DotnetDebugger.cs GetLocalsAsync):**
```csharp
// EXISTING: async state machine detection
var (smMethodName, smTypeFields) = PdbReader.GetMethodTypeFields(dllPath, (int)methodToken);
if (smMethodName == "MoveNext" && smTypeFields.Count > 0)
{
    // reads this.fields
}
```

**Extended flow (CLSR-01 addition):**
```csharp
// NEW: closure display class detection
// smMethodName will be e.g. "<<Main>$>b__0" for lambdas
bool isClosureMethod = smMethodName.Contains(">b__");

if ((smMethodName == "MoveNext" || isClosureMethod) && smTypeFields.Count > 0)
{
    // Same: GetArgument(0) = this = closure/state-machine object
    // Same: skip fields starting with "<>"
    // For closures: captured fields use original names (no <...> wrapping)
}
```

**Key difference from async:** Display class fields have the **original variable names** directly (e.g., `captured`, `capturedStr`). The existing `<> prefix → skip` logic still works for infrastructure. The `<Name> → extract Name` logic is harmless for plain names (no `<` present, goes to `else` branch).

### Pattern 2: Iterator-Specific Field Display (CLSR-02)

**What:** Iterator state machines (MoveNext on `<MethodName>d__N` types) have the same `MoveNext` structure. The existing code already handles them via the `smMethodName == "MoveNext"` check. The only addition needed: expose `<>2__current` as `Current` instead of skipping it.

**Current behavior:** `<>2__current` starts with `<>` → skipped by the `if (fieldName.StartsWith("<>")) continue;` guard.

**Fix:** Add explicit check before the `<>` skip:
```csharp
// Before the "<>" skip:
if (fieldName == "<>2__current")
{
    // Display as "Current" — the last yielded value
    displayName = "Current";
    // do NOT continue — fall through to GetFieldValue
}
else if (fieldName.StartsWith("<>"))
{
    continue; // skip infrastructure
}
```

**Also show `<>1__state` as `_state`** (optional but useful):
```csharp
else if (fieldName == "<>1__state")
{
    displayName = "_state"; // shows iterator position (-2=initial, -1=finished, 0=before first MoveNext, N=at yield N)
}
```

### Pattern 3: Circular Reference Detection (GRAPH-01)

**What:** Thread a `HashSet<ulong>` of visited heap addresses through `ReadValue` and `ReadObject`. Check before recursing into any reference type.

**Implementation signature change:**
```csharp
// Change ReadValue signature (internal, all callers updated)
internal static VariableInfo ReadValue(
    string name,
    ICorDebugValue value,
    int depth = 0,
    HashSet<ulong>? visited = null)

// Initialize in the public entry point
visited ??= new HashSet<ulong>();
```

**Guard in ReadObject (before recursion):**
```csharp
private static VariableInfo ReadObject(
    string name, ICorDebugValue value, int depth, string typeName,
    HashSet<ulong> visited)
{
    // For reference types only (not structs/value types):
    if (typeName != "struct")
    {
        try
        {
            var refVal = (ICorDebugReferenceValue)value;
            refVal.IsNull(out int isNull);
            if (isNull != 0)
                return new VariableInfo(name, typeName, "null", Array.Empty<VariableInfo>());

            refVal.Dereference(out var derefed);

            // CIRCULAR REFERENCE CHECK: get heap address of dereferenced object
            derefed.GetAddress(out ulong addr);
            if (addr != 0 && !visited.Add(addr))
            {
                // Already visited — circular reference detected
                return new VariableInfo(name, typeName, "<circular reference>", Array.Empty<VariableInfo>());
            }

            actualValue = derefed;
        }
        // ... existing catch blocks
    }
    // ... rest of method
}
```

**Important:** `visited.Add(addr)` returns `true` if the item was newly added, `false` if already present. The check `!visited.Add(addr)` correctly detects a repeat visit.

**Address = 0 edge case:** `GetAddress` returns 0 for values in registers or GC handles. Do NOT add 0 to the visited set; skip the circular check for addr == 0.

### Pattern 4: Computed Property Reporting (GRAPH-02)

**What:** After the field enumeration loop in `ReadObjectFields`, enumerate all properties of the type from PE metadata. For each property, if no `<PropName>k__BackingField` was already found in the instance fields, add a `VariableInfo` with value `"<computed>"`.

**Implementation in `ReadObjectFields` after the field loop:**
```csharp
// After the inheritance-walking field loop:
// Add computed properties (properties without a PE backing field)
try
{
    var allFieldNames = new HashSet<string>(
        ReadInstanceFieldsFromPE(dllPath, typedefToken).Values);

    using var propPeReader = new PEReader(File.OpenRead(dllPath));
    var propMetadata = propPeReader.GetMetadataReader();
    int rowNum = (int)(typedefToken & 0x00FFFFFF);
    var propTypeHandle = MetadataTokens.TypeDefinitionHandle(rowNum);
    var propTypeDef = propMetadata.GetTypeDefinition(propTypeHandle);

    foreach (var propHandle in propTypeDef.GetProperties())
    {
        var prop = propMetadata.GetPropertyDefinition(propHandle);
        string propName = propMetadata.GetString(prop.Name);
        string expectedBacking = $"<{propName}>k__BackingField";

        if (!allFieldNames.Contains(expectedBacking))
        {
            // No backing field — computed property
            children.Add(new VariableInfo(propName, "<computed>", "<computed>", Array.Empty<VariableInfo>()));
        }
    }
}
catch { /* property scan is best-effort */ }
```

**Note:** Only enumerate properties from the concrete type's own TypeDef, not from base types (the inheritance walk for fields already handles most cases, and property inheritance is complex). This covers the common case.

### Anti-Patterns to Avoid

- **Re-opening PEReader multiple times in ReadObjectFields:** Currently `ReadInstanceFieldsFromPE` and `GetTypeName` each open a new `PEReader`. For GRAPH-02, avoid a 3rd open — reuse the fields already collected or accept the extra open as best-effort (the PEReader is cheap to open for local files).
- **Tracking HashSet by value identity:** Do NOT use `visited.Contains(value)` (object reference). Use `ulong` addresses from `GetAddress()` — the COM objects are different C# wrappers for the same native pointer.
- **Adding visited set to public API:** Keep `ReadValue` public signature unchanged for callers outside `VariableReader`. Use an optional parameter with default `null` that gets initialized internally.
- **Reporting computed properties for compiler-generated types:** Skip `GetProperties()` enumeration for types whose names start with `<>` (display classes, state machines have no meaningful user properties).

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Property enumeration | Manual `get_PropName` method scan in IL | `TypeDefinition.GetProperties()` from PE metadata | PE properties table is authoritative; method scanning is fragile |
| Closure class detection | String matching on type names | `smMethodName.Contains(">b__")` on method names from `GetMethodTypeFields` | The method name is already retrieved; type name inspection adds complexity |
| Address tracking | Reference equality on COM objects | `ICorDebugValue.GetAddress()` + `HashSet<ulong>` | COM wrappers are different C# objects for same native pointer; `ReferenceEquals` would never detect cycles |

---

## Common Pitfalls

### Pitfall 1: Display Class vs Async State Machine — Different Field Naming
**What goes wrong:** Assuming closure fields follow the `<varName>5__N` hoisted naming pattern used by async state machines.
**Why it happens:** Both are compiler-generated classes, but closures store captured variables with **original names** (`captured`, not `<captured>5__1`).
**How to avoid:** Verified from PE metadata inspection of `closure_test.dll`: `<>c__DisplayClass0_0` has fields named `captured` and `capturedStr` directly. The existing `<>` skip + `<Name>k...` extraction logic is harmless for plain names.
**Warning signs:** Variables showing as empty/skipped when debugging lambdas.

### Pitfall 2: Iterator `<>2__current` Skipped by Existing "<>" Guard
**What goes wrong:** Iterator's `Current` value never shows — skipped by `if (fieldName.StartsWith("<>")) continue;`.
**Why it happens:** Current code skips ALL `<>` prefixed fields as "infrastructure." Iterator's current value field also starts with `<>`.
**How to avoid:** Add explicit checks for `<>2__current` (→ `Current`) and optionally `<>1__state` (→ `_state`) BEFORE the general `<>` skip guard.
**Warning signs:** `debug_variables` on an iterator shows no variables at all.

### Pitfall 3: GetAddress Returns 0 for Enregistered/GCHandle Values
**What goes wrong:** Every reference-type value gets address 0, so visited set never detects cycles correctly, OR all address-0 values get flagged as circular.
**Why it happens:** Values in CPU registers or stored via GC handles don't have a stable heap address.
**How to avoid:** Skip the circular check when `addr == 0`. Do NOT add 0 to the visited set. The `MaxDepth = 3` guard still prevents infinite recursion for these edge cases.
**Warning signs:** All objects reporting `<circular reference>` even when there's no cycle.

### Pitfall 4: visited Set Mutation Across Sibling Fields
**What goes wrong:** Once an object is added to `visited`, its address is never removed. If the same object appears in multiple fields of a parent (like a shared reference — not a cycle), it gets marked as circular on the second appearance.
**Why it happens:** A HashSet tracking for cycle detection behaves differently from a "seen this path" DFS stack.
**How to avoid:** This is acceptable behavior for a debugger — shared references and cycles both get `<circular reference>` after the first read. Document this as a known limitation. The alternative (path-based DFS stack) is significantly more complex.
**Warning signs:** Shared (non-circular) references showing as `<circular reference>`.

### Pitfall 5: Property Enumeration on Compiler-Generated Types
**What goes wrong:** Enumerating properties on `<>c__DisplayClass` or `<<Main>$>d__0` produces confusing output — interface method entries like `System.IDisposable.Dispose` or similar.
**Why it happens:** `typeDef.GetProperties()` includes any property-like entries from interface implementations.
**How to avoid:** Guard the GRAPH-02 property enumeration: skip types whose name starts with `<` (compiler-generated). Only run on user-defined types.
**Warning signs:** Iterator/closure fields showing redundant entries.

### Pitfall 6: Closures in Non-Top-Level Programs
**What goes wrong:** In a class method (not top-level program), the method containing the lambda is named normally, but the lambda method is still `b__N` on the display class. The display class name changes but `>b__` in the method name is stable.
**Why it happens:** The `b__` naming is Roslyn's convention for closure lambdas regardless of enclosing type.
**How to avoid:** Rely on `smMethodName.Contains(">b__")` — verified to work for both top-level and class-method lambdas.

---

## Code Examples

Verified patterns from live PE metadata inspection (`closure_test.dll` and `HelloDebug.dll`, .NET 10).

### Closure Detection — Verified Field Names
```
Type: <>c__DisplayClass0_0
Fields:
  [instance] captured        ← original name, no angle brackets
  [instance] capturedStr     ← original name, no angle brackets
Methods:
  .ctor
  <<Main>$>b__0              ← method name contains ">b__"
```

### Iterator State Machine — Verified Field Names
```
Type: <<<Main>$>g__GetNumbers|0_1>d
Fields:
  [instance] <>1__state          ← execution position (-2=initial, -1=done, N=at yield N)
  [instance] <>2__current        ← last yielded value → show as "Current"
  [instance] <>l__initialThreadId ← infrastructure, skip
Methods:
  MoveNext
  System.Collections.Generic.IEnumerator<System.Int32>.get_Current
  ...
```

### Async State Machine — Verified Field Names (already handled)
```
Type: <<Main>$>d__0
Fields:
  [instance] <>1__state          ← state machine position
  [instance] <>t__builder        ← infrastructure, skip
  [instance] <counter>5__1       ← hoisted variable → display as "counter"
  [instance] <>u__1              ← infrastructure, skip
```

### Computed Property Detection — Verified
```csharp
// System.Reflection.Metadata API — works on .NET 10
foreach (var propHandle in typeDef.GetProperties())
{
    var prop = metadata.GetPropertyDefinition(propHandle);
    string propName = metadata.GetString(prop.Name);
    string expectedBacking = $"<{propName}>k__BackingField";
    bool hasField = instanceFieldNames.Contains(expectedBacking);
    // hasField=false → computed property → add "<computed>" entry
}
```
Verified: `MyClass.Name` → `hasBackingField=true`, `MyClass.FullName` → `hasBackingField=false`.

### Circular Reference Guard — GetAddress Pattern
```csharp
// ICorDebugValue.GetAddress already declared in ICorDebug.cs line 260:
// void GetAddress(out ulong pAddress);

derefed.GetAddress(out ulong addr);
if (addr != 0 && !visited.Add(addr))
{
    return new VariableInfo(name, typeName, "<circular reference>", Array.Empty<VariableInfo>());
}
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `MaxDepth=3` stops recursion | `HashSet<ulong>` visited set detects cycles | Phase 6 | No false `<max depth>` for non-deep circular graphs |
| Properties absent from output | Computed properties show `<computed>` | Phase 6 | Users see all properties, not just fields |
| Iterator Current invisible | `<>2__current` shown as `Current` | Phase 6 | Iterator inspection is meaningful |

---

## Open Questions

1. **`_state` field display for iterators**
   - What we know: `<>1__state` values: `-2` = not started, `-1` = finished, `0...N` = at yield N
   - What's unclear: Is showing `_state` as an integer useful, or is it too low-level?
   - Recommendation: Show it as `_state` for completeness; can be filtered later.

2. **Shared references vs cycles in visited set**
   - What we know: A `HashSet` of addresses treats both shared and circular the same (after first encounter).
   - What's unclear: Will users be confused when a shared reference shows as `<circular reference>`?
   - Recommendation: Accept the limitation for now; document it in `VariableInfo` value string. A path-based approach is significantly more complex and not required by GRAPH-01.

3. **Display class detection robustness**
   - What we know: Method name contains `>b__` for lambdas in all test cases.
   - What's unclear: Does this hold for local functions? Local functions compile differently (not display classes).
   - Recommendation: Only trigger the closure path when method name contains `>b__` AND declaring type name contains `c__DisplayClass`. Local functions do not use display classes.

---

## Existing Code — Key Facts for Planning

### GetLocalsAsync already handles MoveNext (lines 688-845)
The exact pattern for reading `this` fields in a state machine is implemented and working. CLSR-01 and CLSR-02 need to extend this with:
- Detect `smMethodName.Contains(">b__")` for closures
- Expose `<>2__current` as `Current` for iterators

### VariableReader.ReadObjectFields (lines 380-460)
- Already walks inheritance chain
- Already skips `<>` prefixed fields and extracts names from `<Name>k...BackingField` patterns
- GRAPH-01 adds `HashSet<ulong> visited` parameter threaded through `ReadValue`
- GRAPH-02 adds property enumeration after the field loop

### ICorDebugValue.GetAddress already declared (ICorDebug.cs line 260)
No interface changes needed for circular reference detection.

### PdbReader.GetMethodTypeFields returns declaring type fields
This is what populates `smTypeFields` and what the closure detection can directly use.

---

## HelloDebug Sections to Add (TEST-08)

Sections 13-16 already exist. Need sections 17, 18, 19:

**Section 17 — Closure:**
```csharp
// BP-17: Breakpoint inside lambda. Expected: captured variables visible.
int capturedValue = 100;
string capturedName = "world";
Action action = () =>
{
    Console.WriteLine($"{capturedName}: {capturedValue}");  // <── BP-17
};
action();
```

**Section 18 — Iterator:**
```csharp
// BP-18: Breakpoint here after starting iteration.
// Expected: Current visible, _state > 0.
var iter = GetValues().GetEnumerator();
iter.MoveNext();
// Set BP here and inspect 'iter' — should show Current=10, _state fields
Console.WriteLine($"[18] Iterator Current: {iter.Current}");  // <── BP-18

IEnumerable<int> GetValues()
{
    yield return 10;
    yield return 20;
    yield return 30;
}
```

**Section 19 — Circular reference:**
```csharp
// BP-19: Breakpoint here. Inspect 'circular' — should show <circular reference> not crash.
var circular = new CircularRef();
circular.Self = circular;
Console.WriteLine($"[19] Circular ref: {circular.Value}");  // <── BP-19

class CircularRef
{
    public int Value = 42;
    public CircularRef? Self;
}
```

---

## Sources

### Primary (HIGH confidence)
- Live PE metadata inspection of `closure_test.dll` (net10.0 Debug build) — exact field names verified
- Live PE metadata inspection of `HelloDebug.dll` (net10.0 Debug build) — async state machine fields verified
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/VariableReader.cs` — current implementation
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` — GetLocalsAsync at lines 688-845
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` line 260 — GetAddress declaration

### Secondary (MEDIUM confidence)
- [csharpindepth.com — Iterator Block Implementation](https://csharpindepth.com/articles/IteratorBlockImplementation) — field names `<>1__state`, `<>2__current`, `<>l__initialThreadId` (verified against live binary)
- [tearth.dev — Magic Behind Closures](https://tearth.dev/posts/magic-behind-closures/) — `<>c__DisplayClass{N}_{M}` naming and direct field names (verified against live binary)
- [Microsoft Docs — ICorDebugValue::GetAddress](https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugvalue-getaddress-method) — returns `CORDB_ADDRESS` (ulong), 0 when unavailable

### Tertiary (LOW confidence — not needed; all critical facts verified from PRIMARY sources)
- None required

---

## Metadata

**Confidence breakdown:**
- Closure field names: HIGH — directly verified from live PE metadata
- Iterator field names: HIGH — directly verified from live PE metadata
- Computed property detection API: HIGH — `TypeDefinition.GetProperties()` tested and working
- Circular reference via GetAddress: HIGH — interface already declared; address semantics documented
- Display class method naming (`>b__`): HIGH — verified from live PE metadata
- HelloDebug section designs: HIGH — based on existing section patterns

**Research date:** 2026-02-23
**Valid until:** 2026-08-23 (stable — compiler naming conventions change only with major Roslyn releases)
