namespace DebuggerNetMcp.Core.Engine;

/// <summary>
/// Represents a breakpoint registered with the debug engine.
/// </summary>
public sealed record BreakpointInfo(
    int Id,
    string File,
    int Line,
    bool IsEnabled);

/// <summary>
/// Represents a single frame in a call stack snapshot.
/// </summary>
public sealed record StackFrameInfo(
    int Index,
    string MethodName,
    string? File,
    int? Line,
    int ILOffset);

/// <summary>
/// Represents an inspected variable or object field, including nested children.
/// Children is empty for primitives and strings.
/// </summary>
public sealed record VariableInfo(
    string Name,
    string Type,
    string Value,
    IReadOnlyList<VariableInfo> Children);

/// <summary>
/// Represents the result of an expression evaluation in the debuggee.
/// </summary>
public sealed record EvalResult(
    bool Success,
    string Value,
    string? ErrorMessage);

/// <summary>
/// Abstract base for all debug events emitted by the debug engine.
/// Sealed subclasses allow exhaustive pattern matching in switch expressions.
/// </summary>
public abstract record DebugEvent;

/// <summary>
/// Emitted when execution stops (breakpoint hit, step complete, explicit pause, etc.).
/// </summary>
public sealed record StoppedEvent(
    string Reason,
    int ThreadId,
    StackFrameInfo? TopFrame) : DebugEvent;

/// <summary>
/// Emitted when a registered breakpoint is hit. Carries the specific breakpoint ID.
/// </summary>
public sealed record BreakpointHitEvent(
    int BreakpointId,
    int ThreadId,
    StackFrameInfo TopFrame) : DebugEvent;

/// <summary>
/// Emitted when the debuggee throws an exception (handled or unhandled).
/// </summary>
public sealed record ExceptionEvent(
    string ExceptionType,
    string Message,
    int ThreadId,
    bool IsUnhandled) : DebugEvent;

/// <summary>
/// Emitted when the debuggee process exits.
/// </summary>
public sealed record ExitedEvent(
    int ExitCode) : DebugEvent;

/// <summary>
/// Emitted when the debuggee writes to stdout, stderr, or the debug console.
/// </summary>
public sealed record OutputEvent(
    string Category,
    string Output) : DebugEvent;
