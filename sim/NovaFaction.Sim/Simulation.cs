using System;
using System.Collections.Generic;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim
{
    /// <summary>
    /// A deterministic match. The only way time passes is <see cref="Tick"/>, called once per tick
    /// (20 per second) with that tick's commands. Same setup + seed + commands = same result on
    /// every device (same map and unit data too: their content hashes are part of the state hash).
    /// </summary>
    public sealed class Simulation
    {
        private readonly CommandLog _log = new CommandLog();

        public Simulation(MatchSetup setup, ulong seed)
        {
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            Seed = seed;
            State = new MatchState(seed, setup);
        }

        public MatchSetup Setup { get; }
        public MatchRules Rules => Setup.Rules;
        public MapDefinition Map => State.Map.Definition;
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
        /// Order of work: apply commands (canonical order), create units of zero-delay deploys, move
        /// units, accrue income, advance the tick and clock, create units whose spawn delay is over,
        /// then handle clock expiry. A deploy on tick N with a delay of D ticks therefore creates its
        /// units at time N + D: for D &gt; 0 they are in the state once <c>State.Tick</c> reaches N + D
        /// and have not moved yet; for D = 0 they appear during tick N and already move on it.
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
            FireDueSpawns(); // only zero-delay deploys from this tick are due here

            UnitMovement.Tick(State, Rules);

            foreach (PlayerState player in State.Players)
            {
                AccrueIncome(player);
            }

            State.Tick++;
            State.ClockRemainingTicks--;
            FireDueSpawns();
            if (State.ClockRemainingTicks == 0)
            {
                OnClockExpired();
            }
        }

        /// <summary>Runs a fresh match from a recorded log and returns it.</summary>
        public static Simulation Replay(MatchSetup setup, ulong seed, CommandLog log)
        {
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }
            var sim = new Simulation(setup, seed);
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
                    DeployCard(player, command);
                    break;
                case CommandType.LeaderAbility:
                    // TODO(M1 leaders): check leader is deployed and ability is off cooldown.
                    break;
            }
        }

        /// <summary>
        /// A deploy is valid when the hand slot holds a card, the player has at least its cost in gold,
        /// and the target is deployable for the player right now. Invalid deploys only bump
        /// <see cref="PlayerState.IgnoredDeploys"/>. A valid deploy pays, cycles the hand and queues the spawn.
        /// </summary>
        private void DeployCard(PlayerState player, Command command)
        {
            UnitDefinition? card = player.Cards.GetSlot(command.HandSlot);
            if (card == null
                || player.Gold < Fix.FromInt(card.Cost)
                || !State.Map.IsDeployable(player.Index, command.Target))
            {
                player.IgnoredDeploys++;
                return;
            }
            player.Gold -= Fix.FromInt(card.Cost);
            player.Cards.Play(command.HandSlot);
            State.PendingSpawnList.Add(new PendingSpawn(State.Tick + Rules.DeploySpawnDelayTicks, player.Index, card,
                command.Target));
        }

        /// <summary>Creates the units of every pending deploy that is due, in deploy order.</summary>
        private void FireDueSpawns()
        {
            List<PendingSpawn> pending = State.PendingSpawnList;
            int kept = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingSpawn spawn = pending[i];
                if (spawn.SpawnTick > State.Tick)
                {
                    pending[kept++] = spawn;
                    continue;
                }
                FixVector2[] positions = UnitMovement.SpawnPositions(State.Map.Grid, spawn.Owner, spawn.Target,
                    spawn.Definition.SpawnCount, Rules.UnitSpawnSpacing);
                foreach (FixVector2 position in positions)
                {
                    State.AddUnit(spawn.Owner, spawn.Definition, position);
                }
            }
            pending.RemoveRange(kept, pending.Count - kept);
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
