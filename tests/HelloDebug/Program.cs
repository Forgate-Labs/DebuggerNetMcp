// HelloDebug — test application for DebuggerNetMcp
// Each section is designed to exercise a different debugger feature.
// Set breakpoints on the marked lines and inspect variables.
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

Console.WriteLine("[HelloDebug] Session complete");

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

// ─── Types ───────────────────────────────────────────────────────────────────
record Address(string Street, string City);

record Person(string Name, int Age, Address Home);

class Node
{
    public int Value { get; }
    public Node? Next { get; }
    public Node(int value, Node? next) { Value = value; Next = next; }
}
