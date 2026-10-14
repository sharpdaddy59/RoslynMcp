using ImpactLib;

namespace ImpactApp;

public static class Program
{
    public static void Main() => Console.WriteLine(Pipeline.Execute(new UpperStage(), "hi"));
}
