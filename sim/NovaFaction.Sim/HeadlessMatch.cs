using System;
using System.Collections.Generic;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Controllers;

namespace NovaFaction.Sim
{
    /// <summary>The outcome of a match run to the end by <see cref="HeadlessMatch"/>.</summary>
    public sealed class MatchResult
    {
        internal MatchResult(Simulation simulation)
        {
            Simulation = simulation;
            MatchState state = simulation.State;
            Winner = state.Winner;
            EndReason = state.EndReason;
            TieBreakRule = state.TieBreakRule;
            Ticks = state.Tick;
            FinalHash = simulation.ComputeHash();
        }

        /// <summary>0 or 1.</summary>
        public int Winner { get; }

        public EndReason EndReason { get; }

        public TieBreakRule TieBreakRule { get; }

        /// <summary>Ticks the match lasted.</summary>
        public int Ticks { get; }

        /// <summary>State hash after the last tick.</summary>
        public ulong FinalHash { get; }

        /// <summary>The finished match (state and command log, for replays).</summary>
        public Simulation Simulation { get; }

        public CommandLog Log => Simulation.Log;
    }

    /// <summary>Runs whole matches with no client: bot vs bot, or bots vs scripted commands.</summary>
    public static class HeadlessMatch
    {
        /// <summary>
        /// Runs a match to its end. Players the setup gives a bot are played by it; every other player plays
        /// <paramref name="scripted"/> (their commands, each stamped with the tick it runs on), or nothing.
        /// Every match ends: regulation, then at most sudden death, then the tie-break list.
        /// </summary>
        public static MatchResult Run(MatchSetup setup, ulong seed, IEnumerable<Command>? scripted = null)
        {
            var sim = new Simulation(setup, seed);
            if (scripted != null)
            {
                foreach (Command command in scripted)
                {
                    HumanController human = sim.GetHuman(command.Player)
                        ?? throw new ArgumentException("Scripted command for player " + command.Player
                            + ", who is played by a bot: " + command, nameof(scripted));
                    human.Submit(command);
                }
            }
            while (!sim.IsEnded)
            {
                sim.Tick();
            }
            return new MatchResult(sim);
        }
    }
}
