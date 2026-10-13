using FixtureLib;
using FixtureLib.LegacyShapes;

// move_type_to_file fixture consumers: references the members of Shapes.cs and
// LegacyShapes.cs from another project, so moving a type must keep this compiling.
public static class ShapesUsages
{
    public static string Summarize()
    {
        var circle = new Circle(1.5);
        var bounds = circle.Bounds();
        var side = new Hexagon.Side { Index = 1 };
        return $"{circle.Kind} {circle.Radius} {bounds.Area} {bounds.Label()} " +
               $"{new Triangle().Describe()} {new Hexagon().Describe()} {side.Index} {ShapeKind.Round}";
    }
}
