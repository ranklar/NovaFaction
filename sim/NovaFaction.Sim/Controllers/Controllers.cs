using System;
using System.Collections.Generic;
using NovaFaction.Sim.Commands;

namespace NovaFaction.Sim.Controllers
{
    /// <summary>
    /// The source of one player's commands. <see cref="Simulation.Tick()"/> asks each player's controller for that
    /// tick's commands (player 0 first) before it applies any of them, and records them in the CommandLog like any
    /// other input, so a replay of the log reproduces the match without the controllers.
    /// </summary>
    public interface IController
    {
        /// <summary>The player (0 or 1) this controller issues commands for.</summary>
        int Player { get; }

        /// <summary>
        /// Appends this player's commands for the coming tick (stamped <c>state.Tick</c>) to <paramref name="output"/>.
        /// The state is the start of the tick and must not be changed.
        /// </summary>
        void AddCommands(MatchState state, List<Command> output);
    }

    /// <summary>
    /// A player whose commands come from outside the sim (touch input, the network, a script or a replay). Commands are
    /// queued with <see cref="Submit"/> and handed over on the tick they are stamped with.
    /// </summary>
    public sealed class HumanController : IController
    {
        private readonly List<Command> _pending = new List<Command>();

        public HumanController(int player)
        {
            if (player < 0 || player >= Command.PlayerCount)
            {
                throw new ArgumentOutOfRangeException(nameof(player));
            }
            Player = player;
        }

        public int Player { get; }

        /// <summary>Commands submitted for ticks that have not run yet.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Queues a command for its tick. It must be for this controller's player.</summary>
        public void Submit(Command command)
        {
            if (command.Player != Player)
            {
                throw new ArgumentException("Command for player " + command.Player + " submitted to player " + Player
                    + "'s controller: " + command);
            }
            _pending.Add(command);
        }

        /// <summary>Queues every command of this player in a recorded log (for replays).</summary>
        public void SubmitAll(CommandLog log)
        {
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }
            for (int tick = 0; tick < log.TickCount; tick++)
            {
                foreach (Command command in log.GetCommands(tick))
                {
                    if (command.Player == Player)
                    {
                        _pending.Add(command);
                    }
                }
            }
        }

        /// <summary>
        /// Hands over the commands stamped with the current tick, in submission order. They stay queued until the tick
        /// has run (so a Tick() that throws loses nothing). A queued command for a tick that has already run is an
        /// input-layer bug: this throws.
        /// </summary>
        public void AddCommands(MatchState state, List<Command> output)
        {
            int tick = state.Tick;
            foreach (Command command in _pending)
            {
                if (command.Tick < tick)
                {
                    throw new InvalidOperationException("Command for tick " + command.Tick + " is still queued on tick "
                        + tick + ": " + command);
                }
            }
            foreach (Command command in _pending)
            {
                if (command.Tick == tick)
                {
                    output.Add(command);
                }
            }
        }

        /// <summary>Drops the commands of a tick that has been recorded.</summary>
        internal void Acknowledge(int tick)
        {
            _pending.RemoveAll(command => command.Tick == tick);
        }
    }
}
