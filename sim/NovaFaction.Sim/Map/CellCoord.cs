using System;

namespace NovaFaction.Sim.Map
{
    /// <summary>
    /// A grid cell. X grows to the right, Y grows upward: row 0 is the bottom edge of the map
    /// (player 0's side) and the last row is the top edge (player 1's side).
    /// </summary>
    public readonly struct CellCoord : IEquatable<CellCoord>
    {
        public readonly int X;
        public readonly int Y;

        public CellCoord(int x, int y)
        {
            X = x;
            Y = y;
        }

        public static bool operator ==(CellCoord a, CellCoord b) => a.X == b.X && a.Y == b.Y;

        public static bool operator !=(CellCoord a, CellCoord b) => !(a == b);

        public bool Equals(CellCoord other) => this == other;

        public override bool Equals(object? obj) => obj is CellCoord other && this == other;

        public override int GetHashCode() => unchecked((X * 397) ^ Y);

        public override string ToString() => "(" + X + ", " + Y + ")";
    }

    /// <summary>
    /// An axis-aligned rectangle of cells: X..X+Width-1 by Y..Y+Height-1 (Y upward).
    /// Used for structure footprints and deploy zones.
    /// </summary>
    public readonly struct CellRect : IEquatable<CellRect>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;

        public CellRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>One past the rightmost column.</summary>
        public int XEnd => X + Width;

        /// <summary>One past the top row.</summary>
        public int YEnd => Y + Height;

        public bool Contains(int x, int y) => x >= X && x < XEnd && y >= Y && y < YEnd;

        public bool Contains(CellCoord cell) => Contains(cell.X, cell.Y);

        /// <summary>True if the rectangle is non-empty and lies entirely inside a width x height grid.</summary>
        public bool IsInside(int gridWidth, int gridHeight) =>
            Width >= 1 && Height >= 1 && X >= 0 && Y >= 0
            && (long)X + Width <= gridWidth && (long)Y + Height <= gridHeight;

        public bool Overlaps(CellRect other) =>
            X < other.XEnd && other.X < XEnd && Y < other.YEnd && other.Y < YEnd;

        public static bool operator ==(CellRect a, CellRect b) =>
            a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

        public static bool operator !=(CellRect a, CellRect b) => !(a == b);

        public bool Equals(CellRect other) => this == other;

        public override bool Equals(object? obj) => obj is CellRect other && this == other;

        public override int GetHashCode() => unchecked((((X * 397) ^ Y) * 397 ^ Width) * 397 ^ Height);

        public override string ToString() => "{x " + X + ", y " + Y + ", " + Width + "x" + Height + "}";

        internal void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(X);
            hasher.Add(Y);
            hasher.Add(Width);
            hasher.Add(Height);
        }
    }
}
