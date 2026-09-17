using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Commands
{
    public enum CommandType
    {
        /// <summary>Explicit "no action" input (e.g. a lockstep heartbeat). Valid, has no effect.</summary>
        None = 0,
        /// <summary>Deploy the card in <see cref="Command.HandSlot"/> at <see cref="Command.Target"/>.</summary>
        DeployCard = 1,
        /// <summary>Use the leader's active ability at <see cref="Command.Target"/>.</summary>
        LeaderAbility = 2,
    }

    /// <summary>
    /// One player input, stamped with the tick it executes on.
    /// Canonical order is <see cref="Tick"/>, then <see cref="Player"/>, then <see cref="Sequence"/>
    /// (the player's own submission counter), so both peers apply inputs identically no matter the
    /// order they arrived over the network.
    /// </summary>
    public readonly struct Command : IEquatable<Command>, IComparable<Command>, IStateHashable
    {
        public const int PlayerCount = 2;
        /// <summary>HandSlot value for commands that do not use a hand slot.</summary>
        public const int NoHandSlot = -1;

        public Command(int tick, int player, int sequence, CommandType type, int handSlot, FixVector2 target)
        {
            Tick = tick;
            Player = player;
            Sequence = sequence;
            Type = type;
            HandSlot = handSlot;
            Target = target;
        }

        /// <summary>The tick this command executes on (0 = the first tick).</summary>
        public int Tick { get; }
        /// <summary>0 or 1.</summary>
        public int Player { get; }
        /// <summary>Submission order within the player's commands; unique per player per tick.</summary>
        public int Sequence { get; }
        public CommandType Type { get; }
        /// <summary>0..handSize-1 for DeployCard; <see cref="NoHandSlot"/> otherwise.</summary>
        public int HandSlot { get; }
        /// <summary>World position for DeployCard / LeaderAbility; Zero for None.</summary>
        public FixVector2 Target { get; }

        public static Command None(int tick, int player, int sequence) =>
            new Command(tick, player, sequence, CommandType.None, NoHandSlot, FixVector2.Zero);

        public static Command DeployCard(int tick, int player, int sequence, int handSlot, FixVector2 target) =>
            new Command(tick, player, sequence, CommandType.DeployCard, handSlot, target);

        public static Command LeaderAbility(int tick, int player, int sequence, FixVector2 target) =>
            new Command(tick, player, sequence, CommandType.LeaderAbility, NoHandSlot, target);

        /// <summary>
        /// Returns null if the command is well-formed for the given hand size, otherwise the reason.
        /// This checks shape only (player, type, slot, target); game-rule checks such as gold cost
        /// and deploy zones come with the features that need them.
        /// </summary>
        public string? Validate(int handSize)
        {
            if (Tick < 0)
            {
                return "tick must not be negative.";
            }
            if (Player < 0 || Player >= PlayerCount)
            {
                return "player must be 0 or 1.";
            }
            if (Sequence < 0)
            {
                return "sequence must not be negative.";
            }
            switch (Type)
            {
                case CommandType.None:
                    if (HandSlot != NoHandSlot || Target != FixVector2.Zero)
                    {
                        return "a None command must have no hand slot and a zero target.";
                    }
                    return null;
                case CommandType.DeployCard:
                    if (HandSlot < 0 || HandSlot >= handSize)
                    {
                        return "hand slot must be between 0 and " + (handSize - 1) + ".";
                    }
                    return null;
                case CommandType.LeaderAbility:
                    if (HandSlot != NoHandSlot)
                    {
                        return "a LeaderAbility command must have no hand slot.";
                    }
                    return null;
                default:
                    return "unknown command type " + (int)Type + ".";
            }
        }

        /// <summary>Canonical order: tick, player, sequence.</summary>
        public int CompareTo(Command other)
        {
            if (Tick != other.Tick)
            {
                return Tick < other.Tick ? -1 : 1;
            }
            if (Player != other.Player)
            {
                return Player < other.Player ? -1 : 1;
            }
            if (Sequence != other.Sequence)
            {
                return Sequence < other.Sequence ? -1 : 1;
            }
            return 0;
        }

        public bool Equals(Command other) =>
            Tick == other.Tick && Player == other.Player && Sequence == other.Sequence &&
            Type == other.Type && HandSlot == other.HandSlot && Target == other.Target;

        public override bool Equals(object? obj) => obj is Command other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Tick;
                h = h * 31 + Player;
                h = h * 31 + Sequence;
                h = h * 31 + (int)Type;
                h = h * 31 + HandSlot;
                h = h * 31 + Target.X.GetHashCode();
                return h * 31 + Target.Y.GetHashCode();
            }
        }

        public static bool operator ==(Command a, Command b) => a.Equals(b);
        public static bool operator !=(Command a, Command b) => !a.Equals(b);

        public override string ToString() =>
            "Command(tick " + Tick + ", player " + Player + ", seq " + Sequence + ", " + Type
            + ", slot " + HandSlot + ", target " + Target.X + "," + Target.Y + ")";

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Tick);
            hasher.Add(Player);
            hasher.Add(Sequence);
            hasher.Add((int)Type);
            hasher.Add(HandSlot);
            hasher.Add(Target);
        }

        /// <summary>
        /// Copies and sorts commands into canonical order. Sorting uses an insertion sort, which is
        /// stable and fully determined by <see cref="CompareTo"/>.
        /// </summary>
        public static Command[] SortCanonical(IReadOnlyList<Command> commands)
        {
            var sorted = new Command[commands.Count];
            for (int i = 0; i < sorted.Length; i++)
            {
                Command current = commands[i];
                int j = i - 1;
                while (j >= 0 && sorted[j].CompareTo(current) > 0)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = current;
            }
            return sorted;
        }
    }
}
