// HelloDebug — test application for DebuggerNetMcp
// Each section is designed to exercise a different debugger feature.
// Set breakpoints on the marked lines and inspect variables.
// BP-20: Background thread breakpoint (multi-thread section)
// BP-21: Unhandled exception (terminates session — must be last section)
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

Console.WriteLine("[HelloDebug] Starting debug test session");

// ─── SECTION 1: Primitives ───────────────────────────────────────────────────
// BP-1: Set breakpoint on the line below.
// Expected: int, bool, double, char all readable as-is.
int counter = 0;
bool isActive = true;
double ratio = 3.14159;
char grade = 'A';
Console.WriteLine($"[1] Primitives: counter={counter}, isActive={isActive}, ratio={ratio}, grade={grade}");

// ─── SECTION 2: Strings ──────────────────────────────────────────────────────
// BP-2: Breakpoint here. Inspect 'greeting' and 'multiWord'.
string greeting = "Hello, World!";
string multiWord = "the quick brown fox";
string? nullableStr = null;
Console.WriteLine($"[2] Strings: '{greeting}' / '{multiWord}' / null={nullableStr is null}");

// ─── SECTION 3: Loop + mutation ──────────────────────────────────────────────
// BP-3: Breakpoint inside the loop body. Watch 'counter' increment each step.
for (int i = 0; i < 5; i++)
{
    counter++;                    // <── BP-3 here
    string step = $"step-{i}";
    Console.WriteLine($"[3] Loop i={i}, counter={counter}, step={step}");
}

// ─── SECTION 4: Collections ──────────────────────────────────────────────────
// BP-4: Breakpoint on the line below. Inspect 'numbers' (List) and 'lookup' (Dictionary).
var numbers = new List<int> { 10, 20, 30, 40, 50 };
var lookup = new Dictionary<string, int>
{
    ["one"] = 1,
    ["two"] = 2,
    ["three"] = 3,
};
int[] arr = new int[] { 100, 200, 300 };
Console.WriteLine($"[4] Collections: List={numbers.Count} items, Dict={lookup.Count} items, arr[2]={arr[2]}");  // <── BP-4

// ─── SECTION 5: Object graph ─────────────────────────────────────────────────
// BP-5: Breakpoint here. Inspect 'person' and its nested 'Address'.
var person = new Person("Alice", 30, new Address("Rua das Flores", "São Paulo"));
Console.WriteLine($"[5] Object: {person.Name}, age={person.Age}, city={person.Home.City}");  // <── BP-5

// ─── SECTION 6: Step-into a method ──────────────────────────────────────────
// BP-6: Breakpoint on the call. Step-into to enter Fibonacci().
int fib = Fibonacci(10);       // <── BP-6
Console.WriteLine($"[6] Fibonacci(10) = {fib}");

// ─── SECTION 7: Exception (caught) ──────────────────────────────────────────
// BP-7: Breakpoint on the try block. Step through; inspect 'ex.Message'.
try
{
    int zero = 0;              // <── BP-7
    int result = 42 / zero;   // throws DivideByZeroException
    Console.WriteLine($"[7] Should not reach: {result}");
}
catch (DivideByZeroException ex)
{
    Console.WriteLine($"[7] Caught exception: {ex.Message}");
}

// ─── SECTION 8: Async method ─────────────────────────────────────────────────
// BP-8: Breakpoint on the await. After continue, the result is available.
int asyncResult = await FetchValueAsync(7);  // <── BP-8
Console.WriteLine($"[8] Async result: {asyncResult}");

// ─── SECTION 9: Nested objects + null ────────────────────────────────────────
// BP-9: Breakpoint here. Inspect 'node' — linked list structure.
var node = new Node(1, new Node(2, new Node(3, null)));
Console.WriteLine($"[9] Linked list head={node.Value}, next={node.Next?.Value}, tail={node.Next?.Next?.Value}");  // <── BP-9

// ─── SECTION 13: Struct ──────────────────────────────────────────────────────
// BP-13: Breakpoint here. Inspect 'pt' — a struct with X and Y fields.
// Expected: type="Point", X=3, Y=4 as children.
var pt = new Point(3, 4);
double dist = pt.Distance();
Console.WriteLine($"[13] Struct: Point({pt.X},{pt.Y}), distance={dist:F2}");  // <── BP-13

// ─── SECTION 14: Enum ────────────────────────────────────────────────────────
// BP-14: Breakpoint here. Inspect 'day', 'season', and 'priority'.
// Expected: day="DayOfWeek.Wednesday", season="Season.Summer", priority="Priority.High"
var day = DayOfWeek.Wednesday;    // System enum
var season = Season.Summer;       // User-defined enum
var priority = Priority.High;     // Underlying int = 2
Console.WriteLine($"[14] Enum: day={day}, season={season}, priority={priority}");  // <── BP-14

// ─── SECTION 15: Nullable ────────────────────────────────────────────────────
// BP-15: Breakpoint here. Inspect 'withValue' and 'withoutValue'.
// Expected: withValue=42 (unwrapped int), withoutValue="null"
int? withValue = 42;
int? withoutValue = null;
double? withDouble = 3.14;
Console.WriteLine($"[15] Nullable: withValue={withValue}, withoutValue={withoutValue is null}");  // <── BP-15

// ─── SECTION 16: Static fields ───────────────────────────────────────────────
// BP-16: Breakpoint here. Use debug_evaluate("AppConfig.MaxRetries") and
// debug_evaluate("AppConfig.Version") to read static fields.
// After mutation: MaxRetries=5, Version="1.0.0"
AppConfig.MaxRetries = 5;  // mutate to verify we read current value, not compile-time constant
Console.WriteLine($"[16] Static: MaxRetries={AppConfig.MaxRetries}, Version={AppConfig.Version}");  // <── BP-16

// ─── SECTION 17: Closure (lambda capture) ────────────────────────────────────
// BP-17: Set breakpoint on the Console.WriteLine inside the lambda.
// Expected: capturedValue=100, capturedName="world" visible as locals.
int capturedValue = 100;
string capturedName = "world";
Action action17 = () =>
{
    Console.WriteLine($"[17] Closure: {capturedName}={capturedValue}");  // <── BP-17
};
action17();

// ─── SECTION 18: Iterator (yield return) ─────────────────────────────────────
// BP-18: Set breakpoint on the Console.WriteLine line below (after MoveNext).
// Expected: 'iter' shows Current=10, _state fields visible.
var iter = GetNumbers().GetEnumerator();
iter.MoveNext();
Console.WriteLine($"[18] Iterator Current: {iter.Current}");  // <── BP-18

// ─── SECTION 19: Circular reference ──────────────────────────────────────────
// BP-19: Set breakpoint on the Console.WriteLine below.
// Expected: inspecting 'circObj' shows Self="<circular reference>" not a crash.
var circObj = new CircularRef();
circObj.Self = circObj;
Console.WriteLine($"[19] Circular: Value={circObj.Value}");  // <── BP-19

// ─── Section 20: Multi-thread ────────────────────────────────────────────────
// BP-20: Set breakpoint on the Console.WriteLine inside the thread lambda below.
// Expected: thread_id parameter on debug_variables returns 'threadMessage' from the background thread.
//           debug_stacktrace without thread_id shows both main thread and background thread frames.
{
    string threadMessage = "hello from main";
    var cts20 = new CancellationTokenSource();
    var bgThread = new Thread(() =>
    {
        string threadMessage = "hello from background";  // intentionally shadows outer
        Console.WriteLine($"[20] Background thread: {threadMessage}");  // <── BP-20
        cts20.Token.WaitHandle.WaitOne();
    });
    bgThread.IsBackground = true;
    bgThread.Start();
    Thread.Sleep(50);   // give background thread time to reach BP-20
    cts20.Cancel();
    bgThread.Join();
}

// ─── Section 21: Unhandled exception ─────────────────────────────────────────
// BP-21: No manual breakpoint needed — the throw below is unhandled.
// Expected: ExceptionEvent delivered with exceptionType="System.InvalidOperationException"
//           and message="Section 21 unhandled" — process does NOT exit silently.
// WARNING: This section terminates the HelloDebug process via an unhandled exception.
//          It MUST remain the last section. The ExceptionEvent is the session-ending event.
throw new InvalidOperationException("Section 21 unhandled");

// Console.WriteLine("[HelloDebug] Session complete"); // unreachable after section 21 throw

// ─── Helper methods ──────────────────────────────────────────────────────────
static int Fibonacci(int n)
{
    if (n <= 1) return n;
    int a = 0, b = 1;
    for (int i = 2; i <= n; i++)
    {
        int tmp = a + b;
        a = b;
        b = tmp;
    }
    return b;
}

static async Task<int> FetchValueAsync(int input)
{
    await Task.Delay(10);    // simulate I/O
    return input * input;
}

static IEnumerable<int> GetNumbers()
{
    yield return 10;
    yield return 20;
    yield return 30;
}

// ─── Types ───────────────────────────────────────────────────────────────────
record Address(string Street, string City);

record Person(string Name, int Age, Address Home);

class Node
{
    public int Value { get; }
    public Node? Next { get; }
    public Node(int value, Node? next) { Value = value; Next = next; }
}

// Section 13: struct
struct Point(int X, int Y)
{
    public int X { get; } = X;
    public int Y { get; } = Y;
    public double Distance() => Math.Sqrt(X * X + Y * Y);
}

// Section 14: user enum types
enum Season { Spring, Summer, Autumn, Winter }
enum Priority { Low = 1, High = 2, Critical = 3 }

// Section 16: static fields
static class AppConfig
{
    public static int MaxRetries = 3;
    public static readonly string Version = "1.0.0";
}

// Section 19: circular reference
class CircularRef
{
    public int Value = 42;
    public CircularRef? Self;
}
