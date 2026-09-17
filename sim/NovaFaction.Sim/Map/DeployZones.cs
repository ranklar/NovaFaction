using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Map
{
    /// <summary>
    /// Where a player may drop units: inside one of the map's base rectangles for that player or one
    /// of the rectangles unlocked by destroying an enemy tower, and never on a cell that is not
    /// walkable ('#' terrain or a standing structure).
    /// </summary>
    public sealed class DeployZones
    {
        private readonly Grid _grid;
        private readonly MapDefinition _map;

        public DeployZones(MapDefinition map, Grid grid)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            if (grid.Width != map.Width || grid.Height != map.Height)
            {
                throw new ArgumentException("Grid and map sizes differ.", nameof(grid));
            }
        }

        public IReadOnlyList<CellRect> GetBaseZones(int player) => _map.GetDeployZones(player);

        /// <param name="player">0 or 1.</param>
        /// <param name="worldPosition">Where the unit would appear.</param>
        /// <param name="unlockedZones">Extra rectangles this player has earned; may be null or empty.</param>
        public bool IsDeployable(int player, FixVector2 worldPosition, IReadOnlyList<CellRect>? unlockedZones)
        {
            MapDefinition.CheckPlayer(player);
            CellCoord cell = _grid.WorldToCell(worldPosition);
            if (!_grid.IsWalkable(cell))
            {
                return false;
            }
            foreach (CellRect zone in _map.GetDeployZones(player))
            {
                if (zone.Contains(cell))
                {
                    return true;
                }
            }
            if (unlockedZones != null)
            {
                for (int i = 0; i < unlockedZones.Count; i++)
                {
                    if (unlockedZones[i].Contains(cell))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
