namespace FixtureLib;

// Unrelated type whose name happens to contain the substring "Process" - renaming the
// Process method (see Processor.cs) must never touch this type or its member.
public class ProcessHelper
{
    public static string Describe() => "helper";
}
