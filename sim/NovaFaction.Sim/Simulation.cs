using System;
using System.Collections.Generic;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim
{
    /// <summary>
    /// A deterministic match. The only way time passes is <see cref="Tick"/>, called once per tick
    /// (20 per second) with that tick's commands. Same rules + seed + commands = same result on
    /// every device.
    /// </summary>
    public sealed class Simulation
    {
        private readonly CommandLog _log = new CommandLog();

        public Simulation(MatchRules rules, ulong seed)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Seed = seed;
            State = new MatchState(seed, rules.MatchLengthTicks, rules.GoldStartingAmount);
        }

        public MatchRules Rules { get; }
        public ulong Seed { get; }
        public MatchState State { get; }

        /// <summary>Every tick's commands so far; replay with <see cref="Replay"/>.</summary>
        public CommandLog Log => _log;

        public bool IsEnded => State.Phase == MatchPhase.Ended;

        /// <summary>Hash of the current state.</summary>
        public ulong ComputeHash() => StateHash.Compute(State);

        /// <summary>
        /// Advances one tick. Every command must be stamped with <c>State.Tick</c> and be well-formed
        /// (see <see cref="Command.Validate"/>); otherwise this throws and the state is unchanged.
        /// Order of work: apply commands (canonical order), accrue income, advance the clock,
        /// then handle clock expiry.
        /// </summary>
        public void Tick(IReadOnlyList<Command> commands)
        {
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (IsEnded)
            {
                throw new InvalidOperationException("The match has ended; Tick() may not be called.");
            }

            // Validate everything before changing any state.
            for (int i = 0; i < commands.Count; i++)
            {
                string? problem = commands[i].Validate(Rules.HandSize);
                if (problem != null)
                {
                    throw new ArgumentException("Invalid command " + commands[i] + ": " + problem, nameof(commands));
                }
            }
            _log.Record(State.Tick, commands);
            IReadOnlyList<Command> ordered = _log.GetCommands(State.Tick);

            for (int i = 0; i < ordered.Count; i++)
            {
                ApplyCommand(ordered[i]);
            }

            foreach (PlayerState player in State.Players)
            {
                AccrueIncome(player);
            }

            State.Tick++;
            State.ClockRemainingTicks--;
            if (State.ClockRemainingTicks == 0)
            {
                OnClockExpired();
            }
        }

        /// <summary>Runs a fresh match from a recorded log and returns it.</summary>
        public static Simulation Replay(MatchRules rules, ulong seed, CommandLog log)
        {
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }
            var sim = new Simulation(rules, seed);
            for (int tick = 0; tick < log.TickCount; tick++)
            {
                sim.Tick(log.GetCommands(tick));
            }
            return sim;
        }

        private void ApplyCommand(Command command)
        {
            PlayerState player = State.GetPlayer(command.Player);
            player.CommandsReceived++;
            switch (command.Type)
            {
                case CommandType.None:
                    break;
                case CommandType.DeployCard:
                    // TODO(M1 units): check hand slot card, gold cost and deploy zone; spend gold;
                    // schedule the spawn after Rules.DeploySpawnDelayTicks.
                    break;
                case CommandType.LeaderAbility:
                    // TODO(M1 leaders): check leader is deployed and ability is off cooldown.
                    break;
            }
        }

        private void AccrueIncome(PlayerState player)
        {
            // Exact income: add per-second income (in raw units) to the carry, then move whole raw
            // units into gold. Over any whole second the player gains exactly goldBaseIncomePerSecond.
            int ticksPerSecond = Rules.TicksPerSecond;
            long carry = player.IncomeRemainder + Rules.GoldBaseIncomePerSecond.Raw;
            long wholeRaw = carry / ticksPerSecond;
            player.IncomeRemainder = carry - wholeRaw * ticksPerSecond;

            Fix gold = player.Gold + Fix.FromRaw(wholeRaw);
            if (gold >= Rules.GoldCap)
            {
                // At the cap, income is lost, including the partial carry.
                gold = Rules.GoldCap;
                player.IncomeRemainder = 0;
            }
            player.Gold = gold;
        }

        private void OnClockExpired()
        {
            switch (State.Phase)
            {
                case MatchPhase.Regulation:
                    // TODO(scoring): if a Keep fell the match already ended. Otherwise compare damage
                    // scores: higher score wins; on an exact tie enter sudden death:
                    //     State.Phase = MatchPhase.SuddenDeath;
                    //     State.ClockRemainingTicks = Rules.SuddenDeathTicks;
                    // Until scoring exists, regulation always ends the match.
                    State.Phase = MatchPhase.Ended;
                    break;
                case MatchPhase.SuddenDeath:
                    // TODO(scoring): the design says "no draws"; decide the tie-break when sudden
                    // death expires with no damage dealt (see docs/design.md open items).
                    State.Phase = MatchPhase.Ended;
                    break;
            }
        }
    }
}
