using System;
using System.Collections.Generic;

namespace NovaFaction.Sim.Commands
{
    /// <summary>
    /// Every tick's commands, in canonical order, from tick 0 onward. Together with the rules and
    /// the seed this is everything needed to replay a match (see <see cref="Simulation.Replay"/>).
    /// </summary>
    public sealed class CommandLog
    {
        private static readonly Command[] NoCommands = Array.Empty<Command>();

        private readonly List<Command[]> _ticks = new List<Command[]>();

        /// <summary>Number of ticks recorded (ticks 0..TickCount-1).</summary>
        public int TickCount => _ticks.Count;

        /// <summary>
        /// Appends the commands for the next tick. <paramref name="tick"/> must equal
        /// <see cref="TickCount"/>, every command must carry that tick, and no player may reuse a
        /// sequence number within the tick. Commands are stored in canonical order.
        /// </summary>
        public void Record(int tick, IReadOnlyList<Command> commands)
        {
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (tick != _ticks.Count)
            {
                throw new ArgumentException("Expected commands for tick " + _ticks.Count + " but got tick " + tick + ".");
            }
            _ticks.Add(Canonicalize(tick, commands));
        }

        /// <summary>The recorded commands for one tick, in canonical order.</summary>
        public IReadOnlyList<Command> GetCommands(int tick)
        {
            if (tick < 0 || tick >= _ticks.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(tick));
            }
            return _ticks[tick];
        }

        /// <summary>Total number of commands across all ticks.</summary>
        public int CommandCount
        {
            get
            {
                int total = 0;
                foreach (Command[] tick in _ticks)
                {
                    total += tick.Length;
                }
                return total;
            }
        }

        /// <summary>Checks tick stamps and duplicates, then returns a sorted copy.</summary>
        internal static Command[] Canonicalize(int tick, IReadOnlyList<Command> commands)
        {
            if (commands.Count == 0)
            {
                return NoCommands;
            }
            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i].Tick != tick)
                {
                    throw new ArgumentException("Command for tick " + commands[i].Tick
                        + " submitted on tick " + tick + ": " + commands[i]);
                }
            }
            Command[] sorted = Command.SortCanonical(commands);
            for (int i = 1; i < sorted.Length; i++)
            {
                if (sorted[i].CompareTo(sorted[i - 1]) == 0)
                {
                    throw new ArgumentException("Duplicate player/sequence on tick " + tick + ": " + sorted[i]);
                }
            }
            return sorted;
        }
    }
}
