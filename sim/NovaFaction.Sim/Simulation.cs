using System;
using System.Collections.Generic;
using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Controllers;
using NovaFaction.Sim.Economy;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Observers;
using NovaFaction.Sim.Spells;
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
        private readonly IController[] _controllers = new IController[Command.PlayerCount];
        private readonly List<Command> _tickCommands = new List<Command>();
        private bool _ticking;

        /// <summary>
        /// A match between the setup's players: a <see cref="BotController"/> for each player the setup gives a bot,
        /// a <see cref="HumanController"/> (commands from outside) for the others.
        /// </summary>
        public Simulation(MatchSetup setup, ulong seed)
        {
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            Seed = seed;
            State = new MatchState(seed, setup);
            for (int player = 0; player < _controllers.Length; player++)
            {
                BotPersonality? bot = setup.GetBot(player);
                _controllers[player] = bot != null
                    ? new BotController(player, bot, seed, State)
                    : (IController)new HumanController(player);
            }
        }

        public MatchSetup Setup { get; }
        public MatchRules Rules => Setup.Rules;
        public MapDefinition Map => State.Map.Definition;
        public ulong Seed { get; }
        public MatchState State { get; }

        /// <summary>Every tick's commands so far; replay with <see cref="Replay"/>.</summary>
        public CommandLog Log => _log;

        public bool IsEnded => State.Phase == MatchPhase.Ended;

        /// <summary>
        /// Optional listener for match events (deploys, casts, deaths, structure damage, captures, chests, gold, the
        /// end). It is not part of the match: with or without one, every tick's state and hash are the same. May be
        /// set or cleared between ticks.
        /// </summary>
        public IMatchObserver? Observer { get; set; }

        /// <summary>The controller issuing the player's commands.</summary>
        public IController GetController(int player) => _controllers[MapDefinition.CheckPlayer(player)];

        /// <summary>The player's <see cref="HumanController"/>, or null when a bot plays them.</summary>
        public HumanController? GetHuman(int player) => GetController(player) as HumanController;

        /// <summary>Advances one tick with only the controllers' commands (see <see cref="Tick(IReadOnlyList{Command})"/>).</summary>
        public void Tick() => Tick(Array.Empty<Command>());

        /// <summary>Hash of the current state.</summary>
        public ulong ComputeHash() => StateHash.Compute(State);

        /// <summary>
        /// Advances one tick. The tick's commands are <paramref name="commands"/> (outside input, allowed only for players
        /// with a <see cref="HumanController"/>) plus what each controller adds: first the human controllers' queued
        /// commands, then each bot's decisions, reading the state as it is before this tick. All of them are recorded in
        /// <see cref="Log"/>. Every command must be stamped with <c>State.Tick</c> and be well-formed
        /// (see <see cref="Command.Validate"/>); otherwise this throws and the match state is unchanged (a bot that
        /// already ran may have advanced its private random stream).
        /// Order of work: apply commands (canonical order; leader abilities act here), create units of zero-delay
        /// deploys, run combat, spells and movement (<see cref="BattleSystem"/>), resolve Keep kills and sudden-death
        /// damage, update mine capture, capture give-ups and chests (<see cref="MapGoldSystem"/>), accrue income (base
        /// and mines), advance the tick and clock, remove expired Rally buffs, then (unless the match just ended)
        /// create units whose spawn delay is over, spawn a due chest wave and handle clock expiry. A deploy on tick N with a delay of D ticks therefore creates its
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
            if (_ticking)
            {
                throw new InvalidOperationException("Tick() may not be called while a tick is running (from an observer).");
            }
            _ticking = true;
            try
            {
                RunTick(commands);
            }
            finally
            {
                _ticking = false;
            }
        }

        private void RunTick(IReadOnlyList<Command> commands)
        {
            // Validate everything before changing any state.
            ValidateCommands(commands, nameof(commands));
            foreach (Command command in commands)
            {
                if (!(_controllers[command.Player] is HumanController))
                {
                    throw new ArgumentException("Player " + command.Player + " is played by a bot; outside command "
                        + command + " is not allowed.", nameof(commands));
                }
            }
            List<Command> all = _tickCommands;
            all.Clear();
            all.AddRange(commands);
            foreach (IController controller in _controllers) // humans first: they have no side effects
            {
                if (controller is HumanController)
                {
                    Collect(controller, all);
                }
            }
            foreach (IController controller in _controllers)
            {
                if (!(controller is HumanController))
                {
                    Collect(controller, all);
                }
            }
            ValidateCommands(all, nameof(commands));
            _log.Record(State.Tick, all);
            foreach (IController controller in _controllers)
            {
                (controller as HumanController)?.Acknowledge(State.Tick);
            }
            IReadOnlyList<Command> ordered = _log.GetCommands(State.Tick);

            for (int i = 0; i < ordered.Count; i++)
            {
                ApplyCommand(ordered[i]);
            }
            FireDueSpawns(); // only zero-delay deploys from this tick are due here

            BattleOutcome outcome = BattleSystem.Tick(State, Rules, Observer);
            ResolveCombat(outcome);
            MapGoldSystem.Tick(State, Rules, Observer);

            foreach (PlayerState player in State.Players)
            {
                AccrueIncome(player);
            }

            State.Tick++;
            State.ClockRemainingTicks--;
            ExpireBuffs();
            if (!IsEnded)
            {
                FireDueSpawns();
                MapGoldSystem.SpawnDueChests(State, Rules);
                if (State.ClockRemainingTicks == 0)
                {
                    OnClockExpired();
                }
            }
            if (IsEnded && Observer != null)
            {
                Observer.OnMatchEnded(new MatchEndedEvent(State.Tick, State.Winner, State.EndReason, State.TieBreakRule,
                    State.GetPlayer(0).Score, State.GetPlayer(1).Score));
            }
        }

        /// <summary>
        /// Runs a fresh match from a recorded log and returns it. Both players get <see cref="HumanController"/>s fed
        /// from the log (bots in the setup are ignored: their decisions are already in the log).
        /// </summary>
        public static Simulation Replay(MatchSetup setup, ulong seed, CommandLog log)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }
            var sim = new Simulation(setup.WithoutBots(), seed);
            for (int player = 0; player < Command.PlayerCount; player++)
            {
                sim.GetHuman(player)!.SubmitAll(log);
            }
            for (int tick = 0; tick < log.TickCount; tick++)
            {
                sim.Tick();
            }
            return sim;
        }

        private void Collect(IController controller, List<Command> all)
        {
            int start = all.Count;
            controller.AddCommands(State, all);
            for (int i = start; i < all.Count; i++)
            {
                if (all[i].Player != controller.Player)
                {
                    throw new InvalidOperationException("Player " + controller.Player
                        + "'s controller issued a command for another player: " + all[i]);
                }
            }
        }

        private void ValidateCommands(IReadOnlyList<Command> commands, string parameter)
        {
            for (int i = 0; i < commands.Count; i++)
            {
                string? problem = commands[i].Validate(Rules.HandSize);
                if (problem != null)
                {
                    throw new ArgumentException("Invalid command " + commands[i] + ": " + problem, parameter);
                }
            }
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
                    UseLeaderAbility(player, command.Target);
                    break;
            }
        }

        /// <summary>
        /// A deploy is valid when the hand slot holds a card, the player has at least its cost in gold, and the target
        /// is allowed: for a unit card, deployable for the player right now; for a spell card, anywhere on the map.
        /// A leader card is also refused while that player's leader is alive on the field or waiting to spawn.
        /// Invalid deploys only bump <see cref="PlayerState.IgnoredDeploys"/>. A valid deploy pays, cycles the hand and
        /// queues the spawn or the spell.
        /// </summary>
        private void DeployCard(PlayerState player, Command command)
        {
            CardDefinition? card = player.Cards.GetSlot(command.HandSlot);
            bool targetOk = card is SpellDefinition
                ? IsOnMap(command.Target)
                : State.Map.IsDeployable(player.Index, command.Target);
            if (card == null || player.Gold < Fix.FromInt(card.Cost) || !targetOk
                || (card.IsLeader && State.HasLeaderOnField(player.Index)))
            {
                player.IgnoredDeploys++;
                return;
            }
            player.Gold -= Fix.FromInt(card.Cost);
            player.Cards.Play(command.HandSlot);
            int level = player.Deck.GetLevel(card);
            if (card is SpellDefinition spell)
            {
                int pulse = spell.IsZone ? Rules.SecondsToTicks(spell.ZoneTickSeconds) : 0;
                var instance = new SpellInstance(State.NextSpellId++, player.Index, spell, command.Target,
                    State.Tick + Rules.SecondsToTicks(spell.CastDelaySeconds), Rules.SecondsToTicks(spell.DurationSeconds),
                    pulse, Rules.LevelFactor(level));
                State.PendingSpellList.Add(instance); // ids only grow, so the list stays sorted
                Observer?.OnSpellCast(new SpellCastEvent(State.Tick, player.Index, spell.Id, instance.Id, level, card.Cost,
                    command.Target, instance.LandTick));
                return;
            }
            State.PendingSpawnList.Add(new PendingSpawn(State.Tick + Rules.DeploySpawnDelayTicks, player.Index,
                (UnitDefinition)card, command.Target, level));
            Observer?.OnCardDeployed(new CardDeployedEvent(State.Tick, player.Index, card.Id, level, card.Cost, command.Target));
        }

        /// <summary>
        /// The leader ability is used when the player's leader has an ability, a living leader of that player is on the
        /// field (Spawning counts) with the target within the ability's range of its center, and the cooldown is over.
        /// Otherwise the command only bumps <see cref="PlayerState.IgnoredAbilities"/>. Heal and Rally act at once, on
        /// units whose center is within the radius of the target now; AreaDamage hits in this tick's combat step.
        /// Ability amounts scale with the leader card's level.
        /// </summary>
        private void UseLeaderAbility(PlayerState player, FixVector2 target)
        {
            UnitDefinition leader = player.Deck.Leader;
            LeaderAbilityDefinition? ability = leader.Ability;
            if (ability == null || State.Tick < player.AbilityReadyTick || !LeaderInRange(player.Index, target, ability.Range))
            {
                player.IgnoredAbilities++;
                return;
            }
            player.AbilityReadyTick = State.Tick + Rules.SecondsToTicks(ability.CooldownSeconds);
            Observer?.OnAbilityCast(new AbilityCastEvent(State.Tick, player.Index, leader.Id, ability.Type, target));
            Fix amount = ability.Amount * Rules.LevelFactor(player.Deck.LeaderLevel);
            switch (ability.Type)
            {
                case AbilityType.AreaDamage:
                    State.AbilityStrikes.Add(new AbilityStrike
                    {
                        Owner = player.Index,
                        Center = target,
                        Radius = ability.Radius,
                        Damage = amount,
                        StructureDamage = amount * ability.StructureDamageMultiplier,
                        Source = DamageSource.ForAbility(player.Index, leader.Id),
                    });
                    break;
                case AbilityType.Rally:
                    var buff = new TimedBuff(ability.Modifiers, State.Tick + Rules.SecondsToTicks(ability.DurationSeconds));
                    foreach (Unit unit in FriendlyUnitsAround(player.Index, target, ability.Radius))
                    {
                        unit.SetBuff(buff); // replaces an earlier Rally: buffs never stack
                    }
                    break;
                case AbilityType.Heal:
                    foreach (Unit unit in FriendlyUnitsAround(player.Index, target, ability.Radius))
                    {
                        unit.Heal(amount);
                    }
                    break;
            }
        }

        private bool LeaderInRange(int player, FixVector2 target, Fix range)
        {
            foreach (Unit unit in State.Units)
            {
                if (unit.Owner == player && unit.Definition.IsLeader && unit.IsAlive
                    && FixVector2.Distance(unit.Position, target) <= range)
                {
                    return true;
                }
            }
            return false;
        }

        private List<Unit> FriendlyUnitsAround(int player, FixVector2 center, Fix radius)
        {
            var result = new List<Unit>();
            foreach (Unit unit in State.Units) // id order
            {
                if (unit.Owner == player && unit.IsAlive && FixVector2.Distance(unit.Position, center) <= radius)
                {
                    result.Add(unit);
                }
            }
            return result;
        }

        /// <summary>Removes every Rally buff whose time is up, restoring the units' stats.</summary>
        private void ExpireBuffs()
        {
            foreach (Unit unit in State.Units)
            {
                unit.ExpireBuff(State.Tick);
            }
        }

        /// <summary>Inside the map rectangle (blocked cells and structures included).</summary>
        private bool IsOnMap(FixVector2 position)
        {
            Fix width = Fix.FromInt(Map.Width) * Map.CellSize;
            Fix height = Fix.FromInt(Map.Height) * Map.CellSize;
            return position.X >= Fix.Zero && position.X < width && position.Y >= Fix.Zero && position.Y < height;
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
                    State.AddUnit(spawn.Owner, spawn.Definition, position, spawn.Level);
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
            if (Observer != null)
            {
                Fix baseReceived = Fix.Min(afterBase, gold) - player.Gold;
                Fix mineReceived = gold - Fix.Min(afterBase, gold);
                if (baseReceived > Fix.Zero)
                {
                    Observer.OnGoldAccrued(new GoldAccruedEvent(State.Tick, player.Index, GoldSource.Base, baseReceived));
                }
                if (mineReceived > Fix.Zero)
                {
                    Observer.OnGoldAccrued(new GoldAccruedEvent(State.Tick, player.Index, GoldSource.Mine, mineReceived));
                }
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
