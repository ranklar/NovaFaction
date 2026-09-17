using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Map
{
    /// <summary>
    /// The map part of a match: the static definition plus what has changed (destroyed structures
    /// and the deploy zones their destruction unlocked). Flow fields are derived data and are not
    /// part of the state hash.
    /// </summary>
    public sealed class MapState : IStateHashable
    {
        private readonly List<CellRect>[] _unlocked = { new List<CellRect>(), new List<CellRect>() };

        internal MapState(MapDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Grid = new Grid(definition);
            FlowFields = new FlowFieldCache(Grid);
            DeployZones = new DeployZones(definition, Grid);
        }

        public MapDefinition Definition { get; }

        public Grid Grid { get; }

        public FlowFieldCache FlowFields { get; }

        public DeployZones DeployZones { get; }

        public bool IsDestroyed(int structureIndex) => Grid.IsStructureDestroyed(structureIndex);

        /// <summary>
        /// Rectangles this player has unlocked by destroying enemy towers, ordered by tower index
        /// (so the list does not depend on the order the towers fell).
        /// </summary>
        public IReadOnlyList<CellRect> GetUnlockedZones(int player) => _unlocked[MapDefinition.CheckPlayer(player)];

        public bool IsDeployable(int player, FixVector2 worldPosition) =>
            DeployZones.IsDeployable(player, worldPosition, GetUnlockedZones(player));

        /// <summary>
        /// Marks a structure destroyed: its footprint becomes walkable (flow fields rebuild on next use)
        /// and, for a forward tower, the other player gains the tower's unlock rectangle.
        /// Destroying an already destroyed structure does nothing.
        /// </summary>
        internal void DestroyStructure(int structureIndex)
        {
            if (!Grid.MarkStructureDestroyed(structureIndex))
            {
                return;
            }
            StructureDefinition s = Definition.Structures[structureIndex];
            if (s.UnlocksDeployZone.HasValue)
            {
                RebuildUnlocked(1 - s.Owner);
            }
        }

        private void RebuildUnlocked(int attacker)
        {
            List<CellRect> list = _unlocked[attacker];
            list.Clear();
            foreach (StructureDefinition s in Definition.Structures) // index order
            {
                if (s.Owner != attacker && s.UnlocksDeployZone.HasValue && Grid.IsStructureDestroyed(s.Index))
                {
                    list.Add(s.UnlocksDeployZone.Value);
                }
            }
        }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Definition.Id);
            hasher.Add(Definition.ContentHash);
            int structureCount = Definition.Structures.Count;
            hasher.Add(structureCount);
            for (int i = 0; i < structureCount; i++)
            {
                hasher.Add(Grid.IsStructureDestroyed(i));
            }
            for (int player = 0; player < 2; player++)
            {
                hasher.Add(_unlocked[player].Count);
                foreach (CellRect zone in _unlocked[player])
                {
                    zone.AppendHash(ref hasher);
                }
            }
        }
    }
}
