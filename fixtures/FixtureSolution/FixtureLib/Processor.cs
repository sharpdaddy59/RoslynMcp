namespace FixtureLib;

/// <summary>
/// Adversarial rename fixture (apply_rename tests). Renaming the Process method must
/// rename this declaration, the override in Processor, and every call site through
/// either type - see the usages in FixtureApp/Program.cs. It must never touch: the
/// word "Process" inside a comment (like this one), the ProcessHelper class, a string
/// literal "Process", or a local variable named process (case-sensitive, distinct symbol).
/// </summary>
public class ProcessorBase
{
    public virtual string Process(string input) => $"base:{input}";

    // Existing member with the exact same signature as Process(string) - a rename
    // target of "Handle" must collide with this and be rejected by MutationApplier.
    public string Handle(string input) => $"handled:{input}";
}

public class Processor : ProcessorBase
{
    public override string Process(string input) => $"derived:{input}";
}
