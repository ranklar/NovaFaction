using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Map
{
    /// <summary>
    /// The live walkability grid of a match, plus cell/world coordinate conversion.
    /// <para>
    /// World space: the bottom-left corner of cell (0, 0) is the world origin; cell (x, y) covers
    /// [x * cellSize, (x + 1) * cellSize) on X and the same on Y. Y grows toward player 1.
    /// </para>
    /// <para>
    /// A cell is walkable when it is inside the map, is not '#' terrain, and is not covered by a
    /// structure that is still standing. Destroying a structure makes its footprint walkable.
    /// </para>
    /// </summary>
    public sealed class Grid
    {
        private readonly bool[] _terrainBlocked;
        private readonly int[] _structureAt; // structure index per cell, or -1
        private readonly CellRect[] _footprints;
        private readonly bool[] _destroyed;

        /// <summary>Builds the starting grid of a map: all structures standing.</summary>
        public Grid(MapDefinition map)
            : this(map.Width, map.Height, map.CellSize, TerrainOf(map), FootprintsOf(map))
        {
        }

        /// <summary>
        /// Builds a grid from raw data (used by tests and tools). <paramref name="terrainBlocked"/> is
        /// row-major with row 0 at the bottom. Footprints must be inside the grid and must not overlap.
        /// </summary>
        internal Grid(int width, int height, Fix cellSize, bool[] terrainBlocked, IReadOnlyList<CellRect> footprints)
        {
            if (width < 1 || height < 1 || width > MapDefinition.MaxSize || height > MapDefinition.MaxSize)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Grid size must be 1.." + MapDefinition.MaxSize + ".");
            }
            if (cellSize.Raw <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size is too small.");
            }
            if (terrainBlocked.Length != width * height)
            {
                throw new ArgumentException("Terrain array has the wrong length.", nameof(terrainBlocked));
            }
            Width = width;
            Height = height;
            CellSize = cellSize;
            _terrainBlocked = (bool[])terrainBlocked.Clone();
            _structureAt = new int[width * height];
            for (int i = 0; i < _structureAt.Length; i++)
            {
                _structureAt[i] = -1;
            }
            _footprints = new CellRect[footprints.Count];
            _destroyed = new bool[footprints.Count];
            for (int s = 0; s < footprints.Count; s++)
            {
                CellRect rect = footprints[s];
                if (!rect.IsInside(width, height))
                {
                    throw new ArgumentException("Footprint " + rect + " is outside the grid.", nameof(footprints));
                }
                _footprints[s] = rect;
                for (int y = rect.Y; y < rect.YEnd; y++)
                {
                    for (int x = rect.X; x < rect.XEnd; x++)
                    {
                        if (_structureAt[y * width + x] >= 0)
                        {
                            throw new ArgumentException("Footprints overlap at (" + x + ", " + y + ").", nameof(footprints));
                        }
                        _structureAt[y * width + x] = s;
                    }
                }
            }
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>World units per cell side.</summary>
        public Fix CellSize { get; }

        public int CellCount => Width * Height;

        public int StructureCount => _footprints.Length;

        /// <summary>
        /// Increases every time walkability changes. Flow fields remember the version they were
        /// built from and are rebuilt when it moves.
        /// </summary>
        public int WalkabilityVersion { get; private set; }

        // ------------------------------------------------------------ coordinates

        public bool IsInBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public bool IsInBounds(CellCoord cell) => IsInBounds(cell.X, cell.Y);

        /// <summary>
        /// The cell containing a world position (exact floor division; positions on a cell edge
        /// belong to the cell above/right). The result may be out of bounds.
        /// </summary>
        public CellCoord WorldToCell(FixVector2 world) =>
            new CellCoord(FloorDiv(world.X.Raw, CellSize.Raw), FloorDiv(world.Y.Raw, CellSize.Raw));

        /// <summary>World position of a cell's center. <see cref="WorldToCell"/> maps it back to the same cell.</summary>
        public FixVector2 CellToWorld(CellCoord cell) => CellToWorld(cell.X, cell.Y);

        public FixVector2 CellToWorld(int x, int y)
        {
            Fix half = Fix.FromRaw(CellSize.Raw / 2);
            return new FixVector2(Fix.FromInt(x) * CellSize + half, Fix.FromInt(y) * CellSize + half);
        }

        /// <summary>World position of a cell's bottom-left corner.</summary>
        public FixVector2 CellToWorldCorner(CellCoord cell) =>
            new FixVector2(Fix.FromInt(cell.X) * CellSize, Fix.FromInt(cell.Y) * CellSize);

        private static int FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0)))
            {
                q--;
            }
            if (q < int.MinValue) return int.MinValue;
            if (q > int.MaxValue) return int.MaxValue;
            return (int)q;
        }

        internal int ToIndex(int x, int y) => y * Width + x;

        // ------------------------------------------------------------ walkability

        /// <summary>True for '#' terrain. Out-of-bounds cells count as blocked.</summary>
        public bool IsTerrainBlocked(int x, int y) => !IsInBounds(x, y) || _terrainBlocked[ToIndex(x, y)];

        public bool IsWalkable(int x, int y)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }
            int i = ToIndex(x, y);
            if (_terrainBlocked[i])
            {
                return false;
            }
            int s = _structureAt[i];
            return s < 0 || _destroyed[s];
        }

        public bool IsWalkable(CellCoord cell) => IsWalkable(cell.X, cell.Y);

        public bool IsWalkableAt(FixVector2 world) => IsWalkable(WorldToCell(world));

        /// <summary>Index of the structure whose footprint covers the cell (standing or destroyed), or -1.</summary>
        public int GetStructureAt(int x, int y) => IsInBounds(x, y) ? _structureAt[ToIndex(x, y)] : -1;

        public CellRect GetFootprint(int structureIndex) => _footprints[CheckStructure(structureIndex)];

        public bool IsStructureDestroyed(int structureIndex) => _destroyed[CheckStructure(structureIndex)];

        /// <summary>
        /// Marks a structure destroyed, which makes its footprint walkable. Returns false (and changes
        /// nothing) if it was already destroyed. Only the sim calls this; use MapState.DestroyStructure
        /// during a match so deploy-zone unlocks stay in step.
        /// </summary>
        internal bool MarkStructureDestroyed(int structureIndex)
        {
            CheckStructure(structureIndex);
            if (_destroyed[structureIndex])
            {
                return false;
            }
            _destroyed[structureIndex] = true;
            WalkabilityVersion++;
            return true;
        }

        private int CheckStructure(int structureIndex)
        {
            if (structureIndex < 0 || structureIndex >= _footprints.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(structureIndex));
            }
            return structureIndex;
        }

        private static bool[] TerrainOf(MapDefinition map)
        {
            var blocked = new bool[map.Width * map.Height];
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    blocked[y * map.Width + x] = map.IsBlockedTerrain(x, y);
                }
            }
            return blocked;
        }

        private static CellRect[] FootprintsOf(MapDefinition map)
        {
            var rects = new CellRect[map.Structures.Count];
            for (int i = 0; i < rects.Length; i++)
            {
                rects[i] = map.Structures[i].Footprint;
            }
            return rects;
        }
    }
}
