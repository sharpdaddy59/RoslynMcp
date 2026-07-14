using FixtureLib;

// Fixture answer key: Greeter is referenced exactly twice in this file
// (the variable type and the constructor); IGreeter once (the cast target)
Greeter greeter = new Greeter();
IGreeter polite = greeter;

Console.WriteLine(polite.Greet("fixture"));

// --- apply_rename adversarial fixture (see Processor.cs) ---
// Answer key: renaming ProcessorBase.Process must touch exactly the 3 call sites below
// (via base type, derived type, and the local variable) - never the string literal,
// this comment, ProcessHelper, or the "process" local variable itself.
ProcessorBase baseRef = new Processor();
Processor derivedRef = new Processor();
var process = derivedRef; // local named process - case-sensitive, must never be renamed
Console.WriteLine(baseRef.Process("via base"));
Console.WriteLine(derivedRef.Process("via derived"));
Console.WriteLine(process.Process("via local"));
Console.WriteLine("Process");
Console.WriteLine(ProcessHelper.Describe());
