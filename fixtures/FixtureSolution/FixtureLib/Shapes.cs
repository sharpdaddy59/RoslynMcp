using System;
using Text = System.String;

namespace FixtureLib;

// move_type_to_file fixture (see MoveTypeToFileTests). Answer key: three top-level types
// in a file-scoped namespace - Circle, Square, ShapeKind - each referenced from another
// type here or from FixtureApp/ShapesUsages.cs. Moving Square must carry both usings, the
// file-scoped namespace form, and Square's doc comment + attribute into Square.cs.

/// <summary>doc</summary>
public sealed class Circle
{
    public Circle(double radius) => Radius = radius;

    public double Radius { get; }

    public ShapeKind Kind => ShapeKind.Round;

    public Square Bounds() => new Square(Radius * 2);
}

/// <summary>A square with a given side length.</summary>
[Serializable]
public readonly record struct Square(double Side)
{
    public double Area => Math.Pow(Side, 2);

    public Text Label() => $"{ShapeKind.Angular} {Side}";
}

public enum ShapeKind
{
    Round,
    Angular
}
