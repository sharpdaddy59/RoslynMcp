namespace FixtureLib.LegacyShapes
{
    using System.Text;

    // move_type_to_file fixture: BLOCK namespace with a using INSIDE the block.
    // Answer key: moving Triangle must produce Triangle.cs in block form carrying
    // the inner 'using System.Text;'; Hexagon stays behind.
    public class Triangle
    {
        public string Describe() => new StringBuilder("triangle:").Append(Hexagon.Sides / 2).ToString();
    }

    public class Hexagon
    {
        public const int Sides = 6;

        public string Describe() => new StringBuilder("hexagon:").Append(Sides).ToString();

        public class Side
        {
            public int Index { get; init; }
        }
    }
}
