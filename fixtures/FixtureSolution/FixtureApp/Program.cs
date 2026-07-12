using FixtureLib;

// Fixture answer key: Greeter is referenced exactly twice in this file
// (the variable type and the constructor); IGreeter once (the cast target)
Greeter greeter = new Greeter();
IGreeter polite = greeter;

Console.WriteLine(polite.Greet("fixture"));
