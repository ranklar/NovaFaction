using System;
using System.Collections.Generic;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Economy;
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
        /// Order of work: apply commands (canonical order), create units of zero-delay deploys, run combat
        /// and movement (<see cref="BattleSystem"/>), resolve Keep kills and sudden-death damage, update mine
        /// capture and collect chests (<see cref="MapGoldSystem"/>), accrue income (base and mines), advance the
        /// tick and clock, then (unless the match just ended) create units whose spawn delay is over, spawn a due
        /// chest wave and handle clock expiry. A deploy on tick N with a delay of D ticks therefore creates its
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

            BattleOutcome outcome = BattleSystem.Tick(State, Rules);
            ResolveCombat(outcome);
            MapGoldSystem.Tick(State, Rules);

            foreach (PlayerState player in State.Players)
            {
                AccrueIncome(player);
            }

            State.Tick++;
            State.ClockRemainingTicks--;
            if (IsEnded)
            {
                return;
            }
            FireDueSpawns();
            MapGoldSystem.SpawnDueChests(State, Rules);
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

        /// <summary>
        /// Exact income: per-second income (in raw units) goes into a carry and whole raw units move into gold, so
        /// over any whole second the player gains exactly the per-second income. Base income and mine income have
        /// separate carries and are added in that order; at the cap the rest is lost (mine gold first) and both
        /// carries are cleared. Mine gold that actually arrived counts toward <see cref="PlayerState.GoldFromMap"/>.
        /// Sudden death multiplies both.
        /// </summary>
        private void AccrueIncome(PlayerState player)
        {
            Fix baseIncome = Rules.GoldBaseIncomePerSecond;
            Fix mineIncome = MapGoldSystem.MineIncome(State, Rules, player.Index);
            if (State.Phase == MatchPhase.SuddenDeath)
            {
                baseIncome *= Rules.SuddenDeathIncomeMultiplier;
                mineIncome *= Rules.SuddenDeathIncomeMultiplier;
            }
            long baseRemainder = player.IncomeRemainder;
            long mineRemainder = player.MineIncomeRemainder;
            Fix baseGold = TakeWhole(baseIncome, ref baseRemainder);
            Fix mineGold = TakeWhole(mineIncome, ref mineRemainder);
            player.IncomeRemainder = baseRemainder;
            player.MineIncomeRemainder = mineRemainder;

            Fix afterBase = player.Gold + baseGold;
            Fix gold = afterBase + mineGold;
            if (gold >= Rules.GoldCap)
            {
                // At the cap, income is lost, including the partial carries.
                gold = Rules.GoldCap;
                player.IncomeRemainder = 0;
                player.MineIncomeRemainder = 0;
            }
            if (gold > afterBase)
            {
                player.GoldFromMap += gold - afterBase;
            }
            player.Gold = gold;
        }

        private Fix TakeWhole(Fix perSecond, ref long remainder)
        {
            int ticksPerSecond = Rules.TicksPerSecond;
            long carry = remainder + perSecond.Raw;
            long wholeRaw = carry / ticksPerSecond;
            remainder = carry - wholeRaw * ticksPerSecond;
            return Fix.FromRaw(wholeRaw);
        }

        /// <summary>
        /// A destroyed Keep ends the match at once in the attacker's favor. If both Keeps fall on the same tick,
        /// the score decides (then the tie-break list). In sudden death, the first tick with any structure
        /// damage ends the match: the player who removed more structure HP that tick wins (the tie-break list
        /// if both removed the same amount).
        /// </summary>
        private void ResolveCombat(BattleOutcome outcome)
        {
            bool[] keepFell = { false, false };
            foreach (int index in outcome.DestroyedStructures)
            {
                StructureDefinition structure = Map.Structures[index];
                if (structure.Kind == StructureKind.Keep)
                {
                    keepFell[structure.Owner] = true;
                }
            }
            if (keepFell[0] && keepFell[1])
            {
                DecideByScore();
                return;
            }
            if (keepFell[0] || keepFell[1])
            {
                End(keepFell[0] ? 1 : 0, EndReason.KeepDestroyed, TieBreakRule.None);
                return;
            }

            Fix damage0 = outcome.StructureDamage[0];
            Fix damage1 = outcome.StructureDamage[1];
            if (State.Phase == MatchPhase.SuddenDeath && (damage0 > Fix.Zero || damage1 > Fix.Zero))
            {
                if (damage0 == damage1)
                {
                    DecideByTieBreak();
                }
                else
                {
                    End(damage0 > damage1 ? 0 : 1, EndReason.FirstDamage, TieBreakRule.None);
                }
            }
        }

        /// <summary>
        /// Regulation over: the higher score wins; an exact tie starts sudden death (or goes straight to the
        /// tie-break list if sudden death lasts 0 s). Sudden death over with no damage: the tie-break list.
        /// </summary>
        private void OnClockExpired()
        {
            switch (State.Phase)
            {
                case MatchPhase.Regulation:
                    if (State.GetPlayer(0).Score != State.GetPlayer(1).Score || Rules.SuddenDeathTicks == 0)
                    {
                        DecideByScore();
                    }
                    else
                    {
                        State.Phase = MatchPhase.SuddenDeath;
                        State.ClockRemainingTicks = Rules.SuddenDeathTicks;
                    }
                    break;
                case MatchPhase.SuddenDeath:
                    DecideByTieBreak();
                    break;
            }
        }

        private void DecideByScore()
        {
            Fix score0 = State.GetPlayer(0).Score;
            Fix score1 = State.GetPlayer(1).Score;
            if (score0 == score1)
            {
                DecideByTieBreak();
            }
            else
            {
                End(score0 > score1 ? 0 : 1, EndReason.Score, TieBreakRule.None);
            }
        }

        /// <summary>
        /// The design doc's tie-breaks, in order: more enemy structures destroyed; higher HP on your own weakest
        /// standing structure (destroyed structures are left out, rule 1 already counted them; a player with no
        /// standing structure counts as 0); more gold received from mines and chests; a coin flip from the match RNG.
        /// </summary>
        private void DecideByTieBreak()
        {
            int destroyed0 = 0, destroyed1 = 0;
            Fix weakest0 = Fix.MaxValue, weakest1 = Fix.MaxValue;
            foreach (StructureState s in State.Structures)
            {
                if (s.IsDestroyed)
                {
                    if (s.Owner == 0) destroyed1++;
                    else destroyed0++;
                }
                else if (s.Owner == 0)
                {
                    weakest0 = Fix.Min(weakest0, s.Hp);
                }
                else
                {
                    weakest1 = Fix.Min(weakest1, s.Hp);
                }
            }
            if (weakest0 == Fix.MaxValue) weakest0 = Fix.Zero;
            if (weakest1 == Fix.MaxValue) weakest1 = Fix.Zero;
            if (destroyed0 != destroyed1)
            {
                End(destroyed0 > destroyed1 ? 0 : 1, EndReason.TieBreak, TieBreakRule.StructuresDestroyed);
                return;
            }
            if (weakest0 != weakest1)
            {
                End(weakest0 > weakest1 ? 0 : 1, EndReason.TieBreak, TieBreakRule.WeakestStructureHp);
                return;
            }
            Fix gold0 = State.GetPlayer(0).GoldFromMap;
            Fix gold1 = State.GetPlayer(1).GoldFromMap;
            if (gold0 != gold1)
            {
                End(gold0 > gold1 ? 0 : 1, EndReason.TieBreak, TieBreakRule.GoldFromMap);
                return;
            }
            End(State.Random.NextInt(0, 2), EndReason.TieBreak, TieBreakRule.CoinFlip);
        }

        private void End(int winner, EndReason reason, TieBreakRule rule)
        {
            State.Phase = MatchPhase.Ended;
            State.Winner = winner;
            State.EndReason = reason;
            State.TieBreakRule = rule;
        }
    }
}
