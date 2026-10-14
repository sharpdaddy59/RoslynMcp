using System.ComponentModel;

namespace FixtureLib;

// get_symbol_info / get_type_hierarchy fixture (see tests/RoslynMcp.Tests/SymbolInfoToolsTests.cs).
// Hierarchy: IAnimal <- ITrainable; Animal : IAnimal; Dog : Animal, ITrainable; Puppy : Dog.
public interface IAnimal
{
    string Name { get; }
}

public interface ITrainable : IAnimal
{
    void Train(string command);
}

public abstract class Animal : IAnimal
{
    protected Animal(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public abstract string Speak();
}

public class Dog : Animal, ITrainable
{
    public Dog(string name) : base(name)
    {
    }

    public string LastCommand { get; private set; } = "";

    public override string Speak() => "Woof";

    /// <summary>Fetches the item and returns how many tries it took.</summary>
    /// <param name="item">What to fetch.</param>
    [Description("fetch")]
    public virtual int Fetch(string item, int maxTries = 3) => Math.Min(item.Length, maxTries);

    public void Train(string command) => LastCommand = command;
}

public sealed class Puppy : Dog
{
    public Puppy() : base("pup")
    {
    }

    public override int Fetch(string item, int maxTries = 3) => item.Length;
}
