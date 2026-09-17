using System;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Map
{
    /// <summary>
    /// Shortest-path field toward one target footprint (a structure), computed on a <see cref="Grid"/>.
    /// <para>
    /// Integration: Dijkstra from every target cell (cost 0) over walkable cells with 8-neighbor
    /// moves. Straight steps cost 1, diagonal steps cost <see cref="DiagonalCost"/>. A diagonal step
    /// is only allowed when both cells it passes beside are open (walkable or part of the target),
    /// so paths never cut the corner of a blocked cell. The target's own cells count as open even
    /// while the structure stands.
    /// </para>
    /// <para>
    /// Direction: each reachable walkable cell points at the neighbor that continues a shortest path.
    /// Ties are broken without any dependence on memory layout: (1) the neighbor whose direction best
    /// matches the straight line from the cell center to the target center, (2) the neighbor to the
    /// left of that line, (3) a fixed direction order. Rules 1 and 2 give the same answer if the whole
    /// map is rotated 180 degrees, so a symmetric map routes both players' units identically.
    /// </para>
    /// A field is a snapshot: rebuild it when the grid's walkability changes (see <see cref="FlowFieldCache"/>).
    /// </summary>
    public sealed class FlowField
    {
        /// <summary>sqrt(2) rounded to the nearest 1/65536. Geometry, not balance data.</summary>
        public static readonly Fix DiagonalCost = Fix.FromRaw(92682);

        private const long Unreachable = long.MaxValue;
        private const sbyte NoDirection = -1;

        // Fixed direction order: E, N, W, S, NE, NW, SW, SE.
        private static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 };
        private static readonly int[] Dy = { 0, 1, 0, -1, 1, 1, -1, -1 };

        private static readonly FixVector2[] Directions = CreateUnitDirections();

        private readonly Grid _grid;
        private readonly long[] _cost;       // raw Fix cells-of-distance; Unreachable if none
        private readonly sbyte[] _direction; // index into Dx/Dy, or NoDirection
        private readonly bool[] _isTarget;

        private FlowField(Grid grid, CellRect target)
        {
            _grid = grid;
            Target = target;
            Version = grid.WalkabilityVersion;
            _cost = new long[grid.CellCount];
            _direction = new sbyte[grid.CellCount];
            _isTarget = new bool[grid.CellCount];
        }

        /// <summary>The footprint this field leads to.</summary>
        public CellRect Target { get; }

        /// <summary>The grid's <see cref="Grid.WalkabilityVersion"/> when this field was built.</summary>
        public int Version { get; }

        /// <summary>True if the grid has changed since this field was built.</summary>
        public bool IsStale => Version != _grid.WalkabilityVersion;

        public static FlowField Compute(Grid grid, CellRect target)
        {
            if (grid == null)
            {
                throw new ArgumentNullException(nameof(grid));
            }
            if (!target.IsInside(grid.Width, grid.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(target), "Target " + target + " is outside the grid.");
            }
            var field = new FlowField(grid, target);
            field.Integrate();
            field.BuildDirections();
            return field;
        }

        // ------------------------------------------------------------ queries

        public bool IsReachable(CellCoord cell) =>
            _grid.IsInBounds(cell) && _cost[_grid.ToIndex(cell.X, cell.Y)] != Unreachable;

        /// <summary>
        /// Path cost in cells from the cell's center to the center of the nearest target cell.
        /// <see cref="Fix.MaxValue"/> if unreachable, out of bounds, or blocked.
        /// </summary>
        public Fix GetCellDistance(CellCoord cell)
        {
            if (!_grid.IsInBounds(cell))
            {
                return Fix.MaxValue;
            }
            long cost = _cost[_grid.ToIndex(cell.X, cell.Y)];
            return cost == Unreachable ? Fix.MaxValue : Fix.FromRaw(cost);
        }

        /// <summary>
        /// <see cref="GetCellDistance"/> of the cell under the position, in world units (cells times
        /// cell size). 0 inside the target; <see cref="Fix.MaxValue"/> if unreachable.
        /// </summary>
        public Fix GetDistance(FixVector2 worldPosition)
        {
            Fix cells = GetCellDistance(_grid.WorldToCell(worldPosition));
            return cells == Fix.MaxValue ? Fix.MaxValue : cells * _grid.CellSize;
        }

        /// <summary>
        /// Unit vector (one of 8 directions) to move along from the cell under the position.
        /// Zero inside the target, and where the target cannot be reached (blocked cells, out of bounds).
        /// </summary>
        public FixVector2 GetDirection(FixVector2 worldPosition) => GetCellDirection(_grid.WorldToCell(worldPosition));

        public FixVector2 GetCellDirection(CellCoord cell)
        {
            if (!_grid.IsInBounds(cell))
            {
                return FixVector2.Zero;
            }
            sbyte d = _direction[_grid.ToIndex(cell.X, cell.Y)];
            return d == NoDirection ? FixVector2.Zero : Directions[d];
        }

        /// <summary>The neighbor cell the direction field steps to, or the cell itself if there is none.</summary>
        public CellCoord GetNextCell(CellCoord cell)
        {
            if (!_grid.IsInBounds(cell))
            {
                return cell;
            }
            sbyte d = _direction[_grid.ToIndex(cell.X, cell.Y)];
            return d == NoDirection ? cell : new CellCoord(cell.X + Dx[d], cell.Y + Dy[d]);
        }

        // ------------------------------------------------------------ build

        private bool IsOpen(int x, int y) =>
            _grid.IsInBounds(x, y) && (_isTarget[_grid.ToIndex(x, y)] || _grid.IsWalkable(x, y));

        /// <summary>A step from (x, y) in direction d lands on an open cell without cutting a corner.</summary>
        private bool CanStep(int x, int y, int d)
        {
            int nx = x + Dx[d], ny = y + Dy[d];
            if (!IsOpen(nx, ny))
            {
                return false;
            }
            return d < 4 || (IsOpen(nx, y) && IsOpen(x, ny));
        }

        private void Integrate()
        {
            for (int i = 0; i < _cost.Length; i++)
            {
                _cost[i] = Unreachable;
            }
            var heap = new MinHeap(_cost.Length);
            // Sources: every target cell, in row-major order.
            for (int y = Target.Y; y < Target.YEnd; y++)
            {
                for (int x = Target.X; x < Target.XEnd; x++)
                {
                    int i = _grid.ToIndex(x, y);
                    _isTarget[i] = true;
                    _cost[i] = 0;
                    heap.Push(0, i);
                }
            }

            long straight = Fix.One.Raw;
            long diagonal = DiagonalCost.Raw;
            while (heap.TryPop(out long cost, out int index))
            {
                if (cost != _cost[index])
                {
                    continue; // stale entry
                }
                int x = index % _grid.Width;
                int y = index / _grid.Width;
                for (int d = 0; d < 8; d++)
                {
                    if (!CanStep(x, y, d))
                    {
                        continue;
                    }
                    int n = _grid.ToIndex(x + Dx[d], y + Dy[d]);
                    long next = cost + (d < 4 ? straight : diagonal);
                    if (next < _cost[n])
                    {
                        _cost[n] = next;
                        heap.Push(next, n);
                    }
                }
            }
        }

        private void BuildDirections()
        {
            // Target center and cell centers in half-cell units, so everything stays integer.
            long targetX2 = 2L * Target.X + Target.Width;
            long targetY2 = 2L * Target.Y + Target.Height;
            long straight = Fix.One.Raw;
            long diagonal = DiagonalCost.Raw;

            for (int y = 0; y < _grid.Height; y++)
            {
                for (int x = 0; x < _grid.Width; x++)
                {
                    int i = _grid.ToIndex(x, y);
                    _direction[i] = NoDirection;
                    if (_isTarget[i] || _cost[i] == Unreachable)
                    {
                        continue;
                    }
                    long vx = targetX2 - (2L * x + 1);
                    long vy = targetY2 - (2L * y + 1);
                    int best = -1;
                    for (int d = 0; d < 8; d++)
                    {
                        if (!CanStep(x, y, d))
                        {
                            continue;
                        }
                        int n = _grid.ToIndex(x + Dx[d], y + Dy[d]);
                        if (_cost[n] == Unreachable || _cost[n] + (d < 4 ? straight : diagonal) != _cost[i])
                        {
                            continue; // not on a shortest path
                        }
                        if (best < 0 || Prefer(d, best, vx, vy))
                        {
                            best = d;
                        }
                    }
                    _direction[i] = (sbyte)best; // a reachable non-target cell always has a parent
                }
            }
        }

        /// <summary>True if direction a should win a tie against direction b, given the vector v to the target.</summary>
        private static bool Prefer(int a, int b, long vx, long vy)
        {
            int alignment = CompareAlignment(a, b, vx, vy);
            if (alignment != 0)
            {
                return alignment > 0;
            }
            // Equally aligned directions mirror each other across v: take the one on v's left.
            long crossA = vx * Dy[a] - vy * Dx[a];
            long crossB = vx * Dy[b] - vy * Dx[b];
            if ((crossA > 0) != (crossB > 0))
            {
                return crossA > 0;
            }
            return a < b;
        }

        /// <summary>Compares cos(angle between d and v) for directions a and b, exactly. Positive if a is better aligned.</summary>
        private static int CompareAlignment(int a, int b, long vx, long vy)
        {
            // cos = dot / |d| (|v| is common). |d|^2 is 1 for straight and 2 for diagonal moves.
            long dotA = vx * Dx[a] + vy * Dy[a];
            long dotB = vx * Dx[b] + vy * Dy[b];
            long lenA2 = a < 4 ? 1 : 2;
            long lenB2 = b < 4 ? 1 : 2;
            if (lenA2 == lenB2)
            {
                return dotA.CompareTo(dotB);
            }
            // Compare dotA * sqrt(lenB2) with dotB * sqrt(lenA2) via signed squares.
            int signA = Math.Sign(dotA), signB = Math.Sign(dotB);
            if (signA != signB)
            {
                return signA.CompareTo(signB);
            }
            long left = dotA * dotA * lenB2;
            long right = dotB * dotB * lenA2;
            return signA >= 0 ? left.CompareTo(right) : right.CompareTo(left);
        }

        private static FixVector2[] CreateUnitDirections()
        {
            var result = new FixVector2[8];
            for (int d = 0; d < 8; d++)
            {
                result[d] = new FixVector2(Fix.FromInt(Dx[d]), Fix.FromInt(Dy[d])).Normalized;
            }
            return result;
        }

        /// <summary>Binary min-heap of (cost, cell index), ordered by cost then index.</summary>
        private sealed class MinHeap
        {
            private long[] _keys;
            private int[] _values;
            private int _count;

            public MinHeap(int capacity)
            {
                _keys = new long[Math.Max(capacity, 4)];
                _values = new int[_keys.Length];
            }

            private bool Less(int i, int j) =>
                _keys[i] < _keys[j] || (_keys[i] == _keys[j] && _values[i] < _values[j]);

            private void Swap(int i, int j)
            {
                long k = _keys[i]; _keys[i] = _keys[j]; _keys[j] = k;
                int v = _values[i]; _values[i] = _values[j]; _values[j] = v;
            }

            public void Push(long key, int value)
            {
                if (_count == _keys.Length)
                {
                    Array.Resize(ref _keys, _count * 2);
                    Array.Resize(ref _values, _count * 2);
                }
                int i = _count++;
                _keys[i] = key;
                _values[i] = value;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (!Less(i, parent))
                    {
                        break;
                    }
                    Swap(i, parent);
                    i = parent;
                }
            }

            public bool TryPop(out long key, out int value)
            {
                if (_count == 0)
                {
                    key = 0;
                    value = 0;
                    return false;
                }
                key = _keys[0];
                value = _values[0];
                _count--;
                _keys[0] = _keys[_count];
                _values[0] = _values[_count];
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1, right = left + 1, smallest = i;
                    if (left < _count && Less(left, smallest)) smallest = left;
                    if (right < _count && Less(right, smallest)) smallest = right;
                    if (smallest == i)
                    {
                        break;
                    }
                    Swap(i, smallest);
                    i = smallest;
                }
                return true;
            }
        }
    }

    /// <summary>
    /// One flow field per structure of a grid, built on first use and rebuilt after walkability changes.
    /// </summary>
    public sealed class FlowFieldCache
    {
        private readonly Grid _grid;
        private readonly FlowField?[] _fields;

        public FlowFieldCache(Grid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _fields = new FlowField?[grid.StructureCount];
        }

        /// <summary>Number of fields built so far (for tests and profiling).</summary>
        public int BuildCount { get; private set; }

        /// <summary>The field leading to the structure's footprint, current for the grid's walkability.</summary>
        public FlowField Get(int structureIndex)
        {
            if (structureIndex < 0 || structureIndex >= _fields.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(structureIndex));
            }
            FlowField? field = _fields[structureIndex];
            if (field == null || field.IsStale)
            {
                field = FlowField.Compute(_grid, _grid.GetFootprint(structureIndex));
                _fields[structureIndex] = field;
                BuildCount++;
            }
            return field;
        }
    }
}
