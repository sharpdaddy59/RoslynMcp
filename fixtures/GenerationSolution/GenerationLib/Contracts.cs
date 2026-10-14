namespace GenerationLib;

// v2.2 generation fixture (implement_interface / implement_abstract / generate_overrides).
// This project does NOT compile on purpose: its baseline errors are the members the
// generation tools must produce (5x CS0535 for MemoryRepository, 1x CS0534 for Circle2,
// 1x CS0535 for Ledger). See tests/RoslynMcp.Tests/ImplementationToolsTests.cs.

public interface IReadOnlyRepo<T>
{
    int Count { get; }
}

public interface IRepository<T> : IReadOnlyRepo<T>
{
    string Name { get; }

    /// <summary>Finds the item stored under <paramref name="key"/>.</summary>
    /// <param name="key">The lookup key.</param>
    T? Find(string key);

    void Add(T item);

    T? Current { get; set; }

    event EventHandler? Changed;

    T this[int index] { get; set; }
}

public class MemoryRepository : IRepository<string>
{
    private readonly List<string> _items = new();

    public string Name => "memory";

    public void Add(string item) => _items.Add(item);
}

public abstract class Shape2
{
    public abstract double Area();

    /// <summary>Length of the outline.</summary>
    public abstract double Perimeter { get; }

    /// <summary>Human-readable description of the shape.</summary>
    public virtual string Describe() => $"Shape with area {Area()}";
}

public class Circle2 : Shape2
{
    public double Radius { get; set; }

    public override double Area() => Math.PI * Radius * Radius;
}

public interface INamed
{
    string Name { get; }
}

public class Plain : INamed
{
    public string Name => "plain";
}

public interface IAuditable
{
    void Record(string entry);

    DateTime LastAudit { get; }
}

public partial class Ledger
{
    public void Record(string entry) => Total++;
}
