using System;
using System.Collections.Generic;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Map
{
    public enum StructureKind
    {
        Keep = 0,
        ForwardTower = 1,
    }

    /// <summary>A Keep or forward tower as authored in a map file.</summary>
    public sealed class StructureDefinition
    {
        internal StructureDefinition(int index, string id, StructureKind kind, int owner, CellRect footprint,
            CellRect? unlocksDeployZone)
        {
            Index = index;
            Id = id;
            Kind = kind;
            Owner = owner;
            Footprint = footprint;
            UnlocksDeployZone = unlocksDeployZone;
        }

        /// <summary>Position in the map file's "structures" list. This is the structure's id inside the sim.</summary>
        public int Index { get; }

        /// <summary>Name from the map file, e.g. "tower_0_west".</summary>
        public string Id { get; }

        public StructureKind Kind { get; }

        /// <summary>Owning player, 0 or 1, as written in the map file.</summary>
        public int Owner { get; }

        /// <summary>Cells the structure occupies. They are not walkable while it stands.</summary>
        public CellRect Footprint { get; }

        /// <summary>
        /// Forward towers only: the rectangle the attacker (the other player) may deploy into once
        /// this tower is destroyed. Null for Keeps.
        /// </summary>
        public CellRect? UnlocksDeployZone { get; }
    }

    /// <summary>
    /// A map loaded from content/maps/*.json. Immutable. See docs/design.md ("Map layer") for the
    /// file format. <see cref="FromJson"/> validates everything, including that every structure can
    /// be reached from both players' spawn markers.
    /// </summary>
    public sealed class MapDefinition
    {
        /// <summary>The only "formatVersion" this loader accepts.</summary>
        public const int FormatVersion = 1;

        public const int MaxSize = 256;

        public const char Ground = '.';
        public const char Blocked = '#';
        public const char KeepCell = 'K';
        public const char TowerCell = 'T';
        public const char MineCell = 'M';
        public const char ChestCell = 'C';
        public const char Spawn0Cell = '0';
        public const char Spawn1Cell = '1';

        private const string Legend = ".#KTMC01";

        /// <summary>Smallest allowed cellSize (1/16 world unit).</summary>
        public static readonly Fix MinCellSize = Fix.FromFraction(1, 16);

        public static readonly Fix MaxCellSize = Fix.FromInt(64);

        private const string KeyFormatVersion = "formatVersion";
        private const string KeyId = "id";
        private const string KeyCellSize = "cellSize";
        private const string KeyGrid = "grid";
        private const string KeyStructures = "structures";
        private const string KeyDeployZones = "deployZones";

        private static readonly string[] RootKeys =
            { KeyFormatVersion, KeyId, KeyCellSize, KeyGrid, KeyStructures, KeyDeployZones };

        private char[] _cells = Array.Empty<char>(); // row-major, row 0 = bottom
        private StructureDefinition[] _structures = Array.Empty<StructureDefinition>();
        private readonly CellRect[][] _deployZones = { Array.Empty<CellRect>(), Array.Empty<CellRect>() };
        private readonly CellCoord[][] _spawnPoints = { Array.Empty<CellCoord>(), Array.Empty<CellCoord>() };
        private CellCoord[] _mines = Array.Empty<CellCoord>();
        private CellCoord[] _chestSpawns = Array.Empty<CellCoord>();

        private MapDefinition()
        {
            Id = "";
        }

        public string Id { get; private set; }

        /// <summary>World units per cell side.</summary>
        public Fix CellSize { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>All structures in file order; <see cref="StructureDefinition.Index"/> is the position here.</summary>
        public IReadOnlyList<StructureDefinition> Structures => _structures;

        /// <summary>Gold mine cells, ordered bottom row first, then left to right.</summary>
        public IReadOnlyList<CellCoord> Mines => _mines;

        /// <summary>Chest spawn cells, ordered bottom row first, then left to right.</summary>
        public IReadOnlyList<CellCoord> ChestSpawns => _chestSpawns;

        /// <summary>
        /// Fingerprint of the map's content (grid, structures, zones, cell size, id). Computed from the
        /// parsed data, not the raw file bytes, so whitespace and line endings (which git may change
        /// per platform) do not affect it.
        /// </summary>
        public ulong ContentHash { get; private set; }

        /// <summary>The player's base deploy rectangles, in file order.</summary>
        public IReadOnlyList<CellRect> GetDeployZones(int player) => _deployZones[CheckPlayer(player)];

        /// <summary>The player's '0' / '1' spawn-side markers, bottom row first, then left to right.</summary>
        public IReadOnlyList<CellCoord> GetSpawnPoints(int player) => _spawnPoints[CheckPlayer(player)];

        /// <summary>The legend character at a cell ('.', '#', 'K', 'T', 'M', 'C', '0', '1').</summary>
        public char GetCell(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                throw new ArgumentOutOfRangeException(nameof(x), "Cell (" + x + ", " + y + ") is outside the map.");
            }
            return _cells[y * Width + x];
        }

        /// <summary>True for '#' cells. Structure footprints are not terrain; see <see cref="Grid"/>.</summary>
        public bool IsBlockedTerrain(int x, int y) => GetCell(x, y) == Blocked;

        /// <summary>The structures with the given owner and kind, in file order.</summary>
        public IReadOnlyList<StructureDefinition> GetStructures(int owner, StructureKind kind)
        {
            CheckPlayer(owner);
            var result = new List<StructureDefinition>();
            foreach (StructureDefinition s in _structures)
            {
                if (s.Owner == owner && s.Kind == kind)
                {
                    result.Add(s);
                }
            }
            return result;
        }

        public StructureDefinition GetKeep(int owner) => GetStructures(owner, StructureKind.Keep)[0];

        internal static int CheckPlayer(int player)
        {
            if (player != 0 && player != 1)
            {
                throw new ArgumentOutOfRangeException(nameof(player), "Player must be 0 or 1.");
            }
            return player;
        }

        /// <summary>Parses and validates a map file. Throws <see cref="SimJsonException"/> on any problem.</summary>
        /// <param name="json">The file's text.</param>
        /// <param name="sourceName">Name used in error messages, e.g. "twolane.json".</param>
        public static MapDefinition FromJson(string json, string sourceName = "map.json")
        {
            JsonValue root = SimJson.Parse(json, sourceName);
            if (root.Kind != JsonKind.Object)
            {
                throw root.Error("the map file must be a JSON object.");
            }
            CheckKeys(root, RootKeys, "map");

            var map = new MapDefinition();

            JsonValue version = root.Get(KeyFormatVersion);
            if (version.AsInt() != FormatVersion)
            {
                throw version.Error("formatVersion must be " + FormatVersion + " but is " + version.NumberText + ".");
            }

            JsonValue id = root.Get(KeyId);
            map.Id = id.AsString();
            if (!IsValidId(map.Id))
            {
                throw id.Error("id must be 1-64 characters of a-z, 0-9, '_' or '-'.");
            }

            JsonValue cellSize = root.Get(KeyCellSize);
            map.CellSize = cellSize.AsFix();
            if (map.CellSize < MinCellSize || map.CellSize > MaxCellSize)
            {
                throw cellSize.Error("cellSize must be between " + MinCellSize + " and " + MaxCellSize
                    + " but is " + map.CellSize + ".");
            }

            ReadGrid(map, root.Get(KeyGrid));
            ReadStructures(map, root.Get(KeyStructures));
            ReadDeployZones(map, root.Get(KeyDeployZones));
            CheckSpawnMarkers(map, sourceName);
            CheckReachability(map, sourceName);
            map.ContentHash = map.ComputeContentHash();
            return map;
        }

        // ------------------------------------------------------------------ grid

        private static void ReadGrid(MapDefinition map, JsonValue grid)
        {
            IReadOnlyList<JsonValue> rows = grid.AsArray();
            if (rows.Count == 0)
            {
                throw grid.Error("grid must have at least one row.");
            }
            if (rows.Count > MaxSize)
            {
                throw grid.Error("grid has " + rows.Count + " rows; the maximum is " + MaxSize + ".");
            }
            string first = rows[0].AsString();
            int width = first.Length;
            if (width == 0 || width > MaxSize)
            {
                throw rows[0].Error("grid rows must be 1-" + MaxSize + " characters long.");
            }
            int height = rows.Count;
            var cells = new char[width * height];
            var mines = new List<CellCoord>();
            var chests = new List<CellCoord>();
            var spawns = new[] { new List<CellCoord>(), new List<CellCoord>() };

            // The first row in the file is the top of the map (highest y), so the file reads like
            // the map seen from player 0's side.
            for (int r = 0; r < height; r++)
            {
                string row = rows[r].AsString();
                if (row.Length != width)
                {
                    throw rows[r].Error("grid is not rectangular: row " + (r + 1) + " has " + row.Length
                        + " characters but row 1 has " + width + ".");
                }
                int y = height - 1 - r;
                for (int x = 0; x < width; x++)
                {
                    char c = row[x];
                    if (Legend.IndexOf(c) < 0)
                    {
                        throw rows[r].Error("grid row " + (r + 1) + " has unknown character '" + c + "' at column "
                            + (x + 1) + " (cell " + x + ", " + y + "). Legend: . ground, # blocked, K Keep, "
                            + "T forward tower, M gold mine, C chest spawn, 0/1 player spawn marker.");
                    }
                    cells[y * width + x] = c;
                }
            }

            // Collect markers bottom row first, left to right.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    switch (cells[y * width + x])
                    {
                        case MineCell: mines.Add(new CellCoord(x, y)); break;
                        case ChestCell: chests.Add(new CellCoord(x, y)); break;
                        case Spawn0Cell: spawns[0].Add(new CellCoord(x, y)); break;
                        case Spawn1Cell: spawns[1].Add(new CellCoord(x, y)); break;
                    }
                }
            }

            map.Width = width;
            map.Height = height;
            map._cells = cells;
            map._mines = mines.ToArray();
            map._chestSpawns = chests.ToArray();
            map._spawnPoints[0] = spawns[0].ToArray();
            map._spawnPoints[1] = spawns[1].ToArray();
        }

        // ------------------------------------------------------------ structures

        private static readonly string[] StructureKeys = { "id", "kind", "owner", "footprint", "unlocksDeployZone" };

        private static void ReadStructures(MapDefinition map, JsonValue list)
        {
            IReadOnlyList<JsonValue> items = list.AsArray();
            var structures = new StructureDefinition[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                JsonValue item = items[i];
                if (item.Kind != JsonKind.Object)
                {
                    throw item.Error("each structure must be an object.");
                }
                CheckKeys(item, StructureKeys, "structure");

                JsonValue idValue = item.Get("id");
                string id = idValue.AsString();
                if (!IsValidId(id))
                {
                    throw idValue.Error("structure id must be 1-64 characters of a-z, 0-9, '_' or '-'.");
                }
                for (int j = 0; j < i; j++)
                {
                    if (structures[j].Id == id)
                    {
                        throw idValue.Error("structure id \"" + id + "\" is used twice.");
                    }
                }

                JsonValue kindValue = item.Get("kind");
                StructureKind kind;
                switch (kindValue.AsString())
                {
                    case "keep": kind = StructureKind.Keep; break;
                    case "tower": kind = StructureKind.ForwardTower; break;
                    default:
                        throw kindValue.Error("structure kind must be \"keep\" or \"tower\" but is \""
                            + kindValue.AsString() + "\".");
                }

                JsonValue ownerValue = item.Get("owner");
                int owner = ownerValue.AsInt();
                if (owner != 0 && owner != 1)
                {
                    throw ownerValue.Error("structure owner must be 0 or 1 but is " + owner + ".");
                }

                CellRect footprint = ReadRect(map, item.Get("footprint"), "footprint of \"" + id + "\"");

                CellRect? unlock = null;
                bool hasUnlock = item.TryGet("unlocksDeployZone", out JsonValue unlockValue);
                if (kind == StructureKind.ForwardTower)
                {
                    if (!hasUnlock)
                    {
                        throw item.Error("tower \"" + id + "\" needs an \"unlocksDeployZone\" rectangle.");
                    }
                    unlock = ReadRect(map, unlockValue, "unlocksDeployZone of \"" + id + "\"");
                }
                else if (hasUnlock)
                {
                    throw unlockValue.Error("only towers have \"unlocksDeployZone\"; \"" + id + "\" is a keep.");
                }

                for (int j = 0; j < i; j++)
                {
                    if (structures[j].Footprint.Overlaps(footprint))
                    {
                        throw item.Error("footprint of \"" + id + "\" overlaps \"" + structures[j].Id + "\".");
                    }
                }
                CheckFootprintCells(map, item, id, kind, footprint);
                structures[i] = new StructureDefinition(i, id, kind, owner, footprint, unlock);
            }

            // Every K/T cell must belong to a declared structure of that kind.
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    char c = map._cells[y * map.Width + x];
                    if (c != KeepCell && c != TowerCell)
                    {
                        continue;
                    }
                    StructureKind expected = c == KeepCell ? StructureKind.Keep : StructureKind.ForwardTower;
                    bool covered = false;
                    foreach (StructureDefinition s in structures)
                    {
                        covered |= s.Kind == expected && s.Footprint.Contains(x, y);
                    }
                    if (!covered)
                    {
                        throw list.Error("grid cell (" + x + ", " + y + ") is '" + c + "' but no "
                            + (c == KeepCell ? "keep" : "tower") + " footprint in \"structures\" covers it.");
                    }
                }
            }

            for (int player = 0; player < 2; player++)
            {
                int keeps = 0, towers = 0;
                foreach (StructureDefinition s in structures)
                {
                    if (s.Owner != player) continue;
                    if (s.Kind == StructureKind.Keep) keeps++;
                    else towers++;
                }
                if (keeps != 1 || towers != 2)
                {
                    throw list.Error("player " + player + " must own exactly 1 keep and 2 towers but owns "
                        + keeps + " keep(s) and " + towers + " tower(s).");
                }
            }
            map._structures = structures;
        }

        private static void CheckFootprintCells(MapDefinition map, JsonValue item, string id, StructureKind kind,
            CellRect footprint)
        {
            char expected = kind == StructureKind.Keep ? KeepCell : TowerCell;
            for (int y = footprint.Y; y < footprint.YEnd; y++)
            {
                for (int x = footprint.X; x < footprint.XEnd; x++)
                {
                    char c = map._cells[y * map.Width + x];
                    if (c == expected)
                    {
                        continue;
                    }
                    string where = "cell (" + x + ", " + y + ")";
                    switch (c)
                    {
                        case Blocked:
                            throw item.Error("footprint of \"" + id + "\" covers blocked " + where + ".");
                        case MineCell:
                        case ChestCell:
                        case Spawn0Cell:
                        case Spawn1Cell:
                            throw item.Error("spawn point '" + c + "' at " + where + " is inside the footprint of \""
                                + id + "\"; spawn points must be on open ground.");
                        default:
                            throw item.Error("footprint of \"" + id + "\" covers " + where + " but the grid shows '"
                                + c + "' there; draw the footprint with '" + expected + "'.");
                    }
                }
            }
        }

        // ------------------------------------------------------------ deploy zones

        private static readonly string[] ZoneKeys = { "player", "x", "y", "width", "height" };
        private static readonly string[] RectKeys = { "x", "y", "width", "height" };

        private static void ReadDeployZones(MapDefinition map, JsonValue list)
        {
            var zones = new[] { new List<CellRect>(), new List<CellRect>() };
            foreach (JsonValue item in list.AsArray())
            {
                if (item.Kind != JsonKind.Object)
                {
                    throw item.Error("each deploy zone must be an object.");
                }
                CheckKeys(item, ZoneKeys, "deploy zone");
                JsonValue playerValue = item.Get("player");
                int player = playerValue.AsInt();
                if (player != 0 && player != 1)
                {
                    throw playerValue.Error("deploy zone player must be 0 or 1 but is " + player + ".");
                }
                zones[player].Add(ReadRectFields(map, item, "deploy zone of player " + player));
            }
            for (int player = 0; player < 2; player++)
            {
                if (zones[player].Count == 0)
                {
                    throw list.Error("player " + player + " has no deploy zone.");
                }
                map._deployZones[player] = zones[player].ToArray();
            }
        }

        private static CellRect ReadRect(MapDefinition map, JsonValue value, string what)
        {
            if (value.Kind != JsonKind.Object)
            {
                throw value.Error(what + " must be an object with x, y, width, height.");
            }
            CheckKeys(value, RectKeys, what);
            return ReadRectFields(map, value, what);
        }

        private static CellRect ReadRectFields(MapDefinition map, JsonValue obj, string what)
        {
            var rect = new CellRect(obj.Get("x").AsInt(), obj.Get("y").AsInt(),
                obj.Get("width").AsInt(), obj.Get("height").AsInt());
            if (!rect.IsInside(map.Width, map.Height))
            {
                throw obj.Error(what + " " + rect + " must have a positive size and lie inside the "
                    + map.Width + "x" + map.Height + " grid.");
            }
            return rect;
        }

        // ------------------------------------------------------------ spawn markers

        private static void CheckSpawnMarkers(MapDefinition map, string sourceName)
        {
            for (int player = 0; player < 2; player++)
            {
                if (map._spawnPoints[player].Length == 0)
                {
                    throw new SimJsonException(sourceName, 0, 0,
                        "the grid needs at least one '" + player + "' spawn marker for player " + player + ".");
                }
                foreach (CellCoord spawn in map._spawnPoints[player])
                {
                    bool inZone = false;
                    foreach (CellRect zone in map._deployZones[player])
                    {
                        inZone |= zone.Contains(spawn);
                    }
                    if (!inZone)
                    {
                        throw new SimJsonException(sourceName, 0, 0, "spawn marker '" + player + "' at " + spawn
                            + " is outside player " + player + "'s deploy zones.");
                    }
                }
            }
        }

        private static void CheckReachability(MapDefinition map, string sourceName)
        {
            var grid = new Grid(map);
            foreach (StructureDefinition s in map._structures)
            {
                FlowField field = FlowField.Compute(grid, s.Footprint);
                for (int player = 0; player < 2; player++)
                {
                    foreach (CellCoord spawn in map._spawnPoints[player])
                    {
                        if (!field.IsReachable(spawn))
                        {
                            throw new SimJsonException(sourceName, 0, 0, "structure \"" + s.Id
                                + "\" cannot be reached from player " + player + "'s spawn marker at " + spawn + ".");
                        }
                    }
                }
            }

            // Mines and chest spawns must be reachable too, or a unit could never collect them.
            var markers = new List<CellCoord>(map._mines);
            markers.AddRange(map._chestSpawns);
            foreach (CellCoord marker in markers)
            {
                FlowField field = FlowField.Compute(grid, new CellRect(marker.X, marker.Y, 1, 1));
                for (int player = 0; player < 2; player++)
                {
                    foreach (CellCoord spawn in map._spawnPoints[player])
                    {
                        if (!field.IsReachable(spawn))
                        {
                            throw new SimJsonException(sourceName, 0, 0, (map.GetCell(marker.X, marker.Y) == MineCell
                                ? "gold mine" : "chest spawn") + " at " + marker + " cannot be reached from player "
                                + player + "'s spawn marker at " + spawn + ".");
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------ helpers

        private static void CheckKeys(JsonValue obj, string[] allowed, string what)
        {
            foreach (KeyValuePair<string, JsonValue> member in obj.Members)
            {
                if (Array.IndexOf(allowed, member.Key) < 0)
                {
                    throw member.Value.Error("unknown key \"" + member.Key + "\" in " + what + ".");
                }
            }
            foreach (string key in allowed)
            {
                // unlocksDeployZone is checked per kind by the caller.
                if (key != "unlocksDeployZone" && !obj.TryGet(key, out _))
                {
                    throw obj.Error(what + " is missing required key \"" + key + "\".");
                }
            }
        }

        private static bool IsValidId(string id)
        {
            if (id.Length == 0 || id.Length > 64)
            {
                return false;
            }
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        private ulong ComputeContentHash()
        {
            var h = new StateHasher();
            h.Add(FormatVersion);
            h.Add(Id);
            h.Add(CellSize);
            h.Add(Width);
            h.Add(Height);
            foreach (char c in _cells)
            {
                h.Add((byte)c); // legend characters are all ASCII
            }
            h.Add(_structures.Length);
            foreach (StructureDefinition s in _structures)
            {
                h.Add(s.Id);
                h.Add((int)s.Kind);
                h.Add(s.Owner);
                s.Footprint.AppendHash(ref h);
                h.Add(s.UnlocksDeployZone.HasValue);
                if (s.UnlocksDeployZone.HasValue)
                {
                    s.UnlocksDeployZone.Value.AppendHash(ref h);
                }
            }
            for (int player = 0; player < 2; player++)
            {
                h.Add(_deployZones[player].Length);
                foreach (CellRect zone in _deployZones[player])
                {
                    zone.AppendHash(ref h);
                }
            }
            return h.Value;
        }
    }
}
