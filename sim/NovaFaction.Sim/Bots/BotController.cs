using System;
using System.Collections.Generic;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Controllers;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim.Bots
{
    /// <summary>
    /// A computer player. Once per reaction delay (starting on tick 0) it reads the match and scores candidate actions:
    /// defend a threatened structure, attack the weakest lane, send a capturer toward a mine, fetch a chest on its own
    /// side, cast a spell, and use the leader ability. When the card its attack plan wants most costs more than it can
    /// pay for, it saves toward that card instead of spending the gold on cheaper ones (defence is never held back).
    /// It plays at most one card and one ability per decision, taking the best-scoring action with
    /// probability decisionQuality and otherwise one of the top three at random. It uses only what a player can see
    /// (units, pending deploys, structures, its own hand and gold) and never issues a command it knows the sim would
    /// reject. Its randomness comes from its own <see cref="SimRandom"/>, seeded from the match seed and the player, so
    /// the same match always gets the same decisions; the decisions are recorded in the CommandLog like any input.
    /// </summary>
    public sealed class BotController : IController
    {
        // Brain constants. They shape how the bot reasons, not the game's balance; utilities and threat values are in
        // "gold value" (a unit is worth its card cost split over its spawn count, scaled by the hp it has left).
        private static readonly Fix DefendThresholdMin = Fix.Half; // threat answered at defensiveness 1
        private static readonly Fix DefendThresholdSpan = Fix.FromInt(4); // added at defensiveness 0
        private static readonly Fix DefenderRadius = Fix.FromInt(4); // own units this close to a threat count against it
        private static readonly Fix DefendBaseUtility = Fix.FromInt(10);
        private static readonly Fix DefendProximityRange = Fix.FromInt(6); // threats closer than this are more urgent
        private static readonly Fix MeleeDefendFraction = Fix.Half; // melee defenders drop halfway to the threat
        private static readonly Fix RangedDefendFraction = Fix.FromFraction(1, 4); // ranged ones stay nearer the structure
        private static readonly Fix TankHp = Fix.FromInt(1000); // a unit with this much max hp counts as a tank
        private static readonly Fix FastMoveSpeed = Fix.Parse("1.25");
        private static readonly Fix DpsPerPoint = Fix.FromInt(50);
        private static readonly Fix MatchupBonus = Fix.FromInt(2);
        private static readonly Fix AttackWaitGold = Fix.FromInt(3); // extra gold an aggression-0 bot saves before attacking
        private static readonly Fix TankLeadUtility = Fix.FromInt(4);
        private static readonly Fix SupportUtility = Fix.Parse("4.5");
        private static readonly Fix SecondTankUtility = Fix.FromInt(2);
        private static readonly Fix LoneLeadUtility = Fix.One; // plus 2 * aggression
        private static readonly Fix TankAheadSlack = Fix.FromInt(3); // a tank this far behind the drop point still leads
        private static readonly Fix SupportSpacing = Fix.FromInt(2); // support drops this far behind its tank
        private static readonly Fix SafeMargin = Fix.One; // attack waves drop at least this far outside enemy reach
        private static readonly Fix MineUtility = Fix.FromInt(6); // times mineFocus
        private static readonly Fix MineGuardRadius = Fix.FromInt(6); // an own capturer this close already handles a mine
        private static readonly Fix ChestUtilityPerGold = Fix.FromInt(3); // times the chest's gold and mineFocus
        private static readonly Fix ChestGuardRadius = Fix.FromInt(4); // an own unit this close is already collecting it
        private static readonly Fix ChestSendRadius = Fix.FromInt(6); // farther than this from the chest is not "going for it"
        private static readonly Fix ChestDetourRadius = Fix.FromInt(6); // and it must be this near the lane the bot is pushing
        private static readonly Fix SpellOwnHalfUtility = Fix.FromInt(10);
        private static readonly Fix SpellUtility = Fix.FromInt(3);
        private static readonly Fix OwnHalfSpellValueWeight = Fix.FromInt(3); // a good spell beats a unit drop on the same threat
        private static readonly Fix SpellThresholdAtZeroUsage = Fix.Parse("1.5"); // value needed per gold of cost
        private static readonly Fix KillStructureValue = Fix.FromInt(8);
        private static readonly Fix SuddenDeathStructureValue = Fix.FromInt(100);
        private static readonly Fix AbilityValueThreshold = Fix.FromInt(3); // AreaDamage value needed at spellUsage 0.5
        private static readonly Fix RallyGatherRadius = Fix.FromInt(3);
        private const int RallyMinUnits = 3;
        private static readonly Fix HealMinAmounts = Fix.FromInt(2); // missing hp in radius, in heal amounts
        private static readonly Fix AbilityRangeMargin = Fix.Parse("0.99");
        private const int TopChoices = 3;

        private readonly MatchRules _rules;
        private readonly int _enemy;
        private readonly int _delayTicks;
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<FixVector2> _deployable = new List<FixVector2>();
        private readonly List<AttackOption> _attackOptions = new List<AttackOption>();
        private int _deployableVersion = -1;
        private int _deployableUnlocks = -1;
        private int _nextDecisionTick;

        // Per-decision view.
        private MatchState _s = null!;
        private PlayerState _me = null!;
        private bool _suddenDeath;
        private Fix _reserve;
        private Fix _savingsGoal; // gold held back for the card the attack plan wants but cannot pay for yet
        private Fix _savingsFloor; // while saving, what a non-defensive action must beat to be worth breaking in for
        private Fix _attackKeep; // gold an attack must leave in hand: the reserve plus the aggression wait
        private Fix _aggression;
        private FixVector2 _ownKeepCenter;
        private FixVector2 _enemyKeepCenter;

        public BotController(int player, BotPersonality personality, ulong matchSeed, MatchState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            Player = MapDefinition.CheckPlayer(player);
            Personality = personality ?? throw new ArgumentNullException(nameof(personality));
            _rules = state.Rules;
            if (!_rules.IsWholeTicks(personality.ReactionDelaySeconds))
            {
                throw new ArgumentException("Bot \"" + personality.Id + "\": reactionDelaySeconds must be whole ticks.");
            }
            _delayTicks = Math.Max(1, _rules.SecondsToTicks(personality.ReactionDelaySeconds));
            _enemy = 1 - player;
            Random = new SimRandom(DeriveSeed(matchSeed, player));
        }

        public int Player { get; }

        public BotPersonality Personality { get; }

        /// <summary>The bot's own random stream (not part of the match state; its effects are in the command log).</summary>
        public SimRandom Random { get; }

        /// <summary>Ticks between decisions.</summary>
        public int ReactionDelayTicks => _delayTicks;

        /// <summary>A short description of the last card or ability action taken, for debugging. Not match state.</summary>
        public string LastAction { get; private set; } = "";

        /// <summary>
        /// The bot's seed: a SplitMix64 finalizer over the match seed and the player, so the two bots of a match (and
        /// the match's own RNG) draw unrelated streams.
        /// </summary>
        public static ulong DeriveSeed(ulong matchSeed, int player)
        {
            unchecked
            {
                ulong z = matchSeed + 0x9E3779B97F4A7C15UL * (ulong)(player + 1);
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public void AddCommands(MatchState state, List<Command> output)
        {
            if (state.Phase == MatchPhase.Ended || state.Tick < _nextDecisionTick)
            {
                return;
            }
            _nextDecisionTick = state.Tick + _delayTicks;
            Decide(state, output);
        }

        // ------------------------------------------------------------ decision

        private enum ActionKind
        {
            Card,
            Ability,
        }

        private struct Candidate
        {
            public ActionKind Kind;
            public Fix Utility;
            public int Slot;
            public FixVector2 Target;
            public string Label;
        }

        private void Decide(MatchState state, List<Command> output)
        {
            _s = state;
            _me = state.GetPlayer(Player);
            _suddenDeath = state.Phase == MatchPhase.SuddenDeath;
            // Sudden death: all in. The reserve is spent and the bot attacks as if fully aggressive.
            _reserve = _suddenDeath ? Fix.Zero : Personality.GoldReserve;
            _savingsGoal = Fix.Zero; // AddAttack sets these three; the other card actions then respect them
            _savingsFloor = Fix.MinValue;
            _attackKeep = _reserve;
            _attackOptions.Clear();
            _aggression = _suddenDeath ? Fix.One : Personality.Aggression;
            MapDefinition map = state.Map.Definition;
            _ownKeepCenter = FootprintCenter(map.GetKeep(Player).Footprint);
            _enemyKeepCenter = FootprintCenter(map.GetKeep(_enemy).Footprint);
            RefreshDeployable();

            int sequence = 0;
            _candidates.Clear();
            AddDefense();
            AddAttack(); // sets _savingsGoal, so it must run before the actions that respect it
            AddMines();
            AddChests();
            AddSpells();
            if (TryChoose(out Candidate card) && CanDeploy(card.Slot, card.Target))
            {
                output.Add(Command.DeployCard(state.Tick, Player, sequence++, card.Slot, card.Target));
                LastAction = card.Label;
            }

            _candidates.Clear();
            AddAbility();
            if (TryChoose(out Candidate ability) && CanUseAbility(ability.Target))
            {
                output.Add(Command.LeaderAbility(state.Tick, Player, sequence, ability.Target));
                LastAction = ability.Label;
            }
        }

        /// <summary>Best candidate with probability decisionQuality, else one of the top three at random.</summary>
        private bool TryChoose(out Candidate chosen)
        {
            chosen = default;
            int n = _candidates.Count;
            if (n == 0)
            {
                return false;
            }
            // Stable insertion sort, highest utility first (ties keep generation order).
            for (int i = 1; i < n; i++)
            {
                Candidate current = _candidates[i];
                int j = i - 1;
                while (j >= 0 && _candidates[j].Utility < current.Utility)
                {
                    _candidates[j + 1] = _candidates[j];
                    j--;
                }
                _candidates[j + 1] = current;
            }
            int pick = Random.Chance(Personality.DecisionQuality) ? 0 : Random.NextInt(0, Math.Min(TopChoices, n));
            chosen = _candidates[pick];
            return true;
        }

        private void Add(ActionKind kind, Fix utility, int slot, FixVector2 target, string label)
        {
            _candidates.Add(new Candidate { Kind = kind, Utility = utility, Slot = slot, Target = target, Label = label });
        }

        /// <summary>
        /// Adds an action that is spending the bot's savings (a mine or chest drop, an attacking spell). While the
        /// bot is saving it declines attacks it could pay for, so such an action only goes ahead when it is worth
        /// more than the best attack just declined; otherwise saving would simply push the gold into whatever cheap
        /// action was left, which is the opposite of saving. Defence is never gated this way.
        /// </summary>
        private void AddSpending(Fix utility, int slot, FixVector2 target, string label)
        {
            if (utility > _savingsFloor)
            {
                Add(ActionKind.Card, utility, slot, target, label);
            }
        }

        // ------------------------------------------------------------ (a) + (b) threats and defense

        private sealed class Threat
        {
            public int Structure;
            public Fix Value;
            public Fix SumX;
            public Fix SumY;
            public Fix Distance = Fix.MaxValue;
            public Fix FlyerValue;
            public Fix SwarmValue;
            public Fix TankValue;
            public int Count;
            public int RangedCount;
            public Fix Defenders;
            public FixVector2 Center => Value > Fix.Zero ? new FixVector2(SumX / Value, SumY / Value) : FixVector2.Zero;
        }

        /// <summary>Enemy units and pending deploys on the bot's half, grouped by the nearest own standing structure.</summary>
        private List<Threat> AssessThreats()
        {
            var threats = new List<Threat>();
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == _enemy && unit.IsAlive && IsOnOwnHalf(unit.Position))
                {
                    AddThreat(threats, unit.Definition, unit.Position, ValueOf(unit), unit.MaxHp);
                }
            }
            foreach (PendingSpawn spawn in _s.PendingSpawns)
            {
                if (spawn.Owner == _enemy && IsOnOwnHalf(spawn.Target))
                {
                    UnitDefinition d = spawn.Definition;
                    for (int i = 0; i < d.SpawnCount; i++)
                    {
                        AddThreat(threats, d, spawn.Target, ShareOf(d), d.Hp);
                    }
                }
            }
            foreach (Threat threat in threats)
            {
                FixVector2 center = threat.Center;
                foreach (Unit unit in _s.Units)
                {
                    if (unit.Owner == Player && unit.IsAlive && unit.Definition.TargetPriority == TargetPriority.Any
                        && FixVector2.Distance(unit.Position, center) <= DefenderRadius)
                    {
                        threat.Defenders += ValueOf(unit);
                    }
                }
                foreach (PendingSpawn spawn in _s.PendingSpawns)
                {
                    if (spawn.Owner == Player && spawn.Definition.TargetPriority == TargetPriority.Any
                        && FixVector2.Distance(spawn.Target, center) <= DefenderRadius)
                    {
                        threat.Defenders += Fix.FromInt(spawn.Definition.Cost);
                    }
                }
            }
            return threats;
        }

        private void AddThreat(List<Threat> threats, UnitDefinition d, FixVector2 position, Fix value, Fix maxHp)
        {
            int structure = NearestOwnStructure(position, out Fix distance);
            if (structure < 0)
            {
                return;
            }
            Threat? threat = null;
            foreach (Threat t in threats)
            {
                if (t.Structure == structure)
                {
                    threat = t;
                }
            }
            if (threat == null)
            {
                threat = new Threat { Structure = structure };
                threats.Add(threat);
            }
            threat.Value += value;
            threat.SumX += position.X * value;
            threat.SumY += position.Y * value;
            threat.Distance = Fix.Min(threat.Distance, distance);
            threat.Count++;
            if (d.IsFlying)
            {
                threat.FlyerValue += value;
            }
            if (d.SpawnCount >= 3 || d.Slot == UnitSlot.Swarm)
            {
                threat.SwarmValue += value;
            }
            if (d.Slot == UnitSlot.Tank || maxHp >= TankHp)
            {
                threat.TankValue += value;
            }
            if (d.IsRanged)
            {
                threat.RangedCount++;
            }
        }

        private void AddDefense()
        {
            Fix threshold = DefendThresholdMin + DefendThresholdSpan * (Fix.One - Personality.Defensiveness);
            foreach (Threat threat in AssessThreats())
            {
                Fix net = threat.Value - threat.Defenders;
                if (net < threshold)
                {
                    continue;
                }
                FixVector2 center = threat.Center;
                FixVector2 anchor = NearestFootprintPoint(threat.Structure, center);
                Fix proximity = Fix.Max(Fix.Zero, DefendProximityRange - threat.Distance) / DefendProximityRange;
                for (int slot = 0; slot < _me.Cards.HandSize; slot++)
                {
                    if (!(_me.Cards.Hand[slot] is UnitDefinition card) || !CanAfford(card, Fix.Zero) || IsBlockedLeader(card))
                    {
                        continue;
                    }
                    if (!Counter(card, threat, out Fix matchup))
                    {
                        continue;
                    }
                    Fix fraction = card.IsRanged ? RangedDefendFraction : MeleeDefendFraction;
                    FixVector2 wanted = anchor + (center - anchor) * fraction;
                    if (!FindDeployable(wanted, out FixVector2 target))
                    {
                        continue;
                    }
                    Fix overkill = Fix.Max(Fix.Zero, Fix.FromInt(card.Cost) - net) / Fix.FromInt(4);
                    Fix utility = DefendBaseUtility + net + proximity * MatchupBonus + matchup - overkill;
                    Add(ActionKind.Card, utility, slot, target, "defend " + _s.Map.Definition.Structures[threat.Structure].Id
                        + " with " + card.Id);
                }
            }
        }

        /// <summary>
        /// Matchup rules: anti-air against flyers, splash against swarms, high damage per second against tanks, fast
        /// melee against a lone ranged unit. False when the card cannot fight this threat at all.
        /// </summary>
        private bool Counter(UnitDefinition card, Threat threat, out Fix score)
        {
            score = Fix.Zero;
            if (card.TargetPriority == TargetPriority.StructuresOnly)
            {
                return false;
            }
            bool hitsAir = card.Targets != TargetLayer.Ground;
            bool hitsGround = card.Targets != TargetLayer.Air;
            if (threat.FlyerValue > Fix.Zero)
            {
                if (hitsAir)
                {
                    score += MatchupBonus;
                }
                else if (threat.FlyerValue * Fix.FromInt(2) >= threat.Value)
                {
                    return false; // mostly flyers and this card cannot touch them
                }
                else
                {
                    score -= MatchupBonus;
                }
            }
            if (!hitsGround && threat.FlyerValue < threat.Value)
            {
                score -= MatchupBonus;
            }
            if (threat.SwarmValue > Fix.Zero && card.SplashRadius > Fix.Zero)
            {
                score += MatchupBonus;
            }
            if (threat.TankValue > Fix.Zero)
            {
                Fix dps = card.Damage * Fix.FromInt(card.SpawnCount) / card.AttackIntervalSeconds;
                score += Fix.Min(dps / DpsPerPoint, MatchupBonus + Fix.One);
            }
            if (threat.Count == 1 && threat.RangedCount == 1 && !card.IsRanged && card.MoveSpeed >= FastMoveSpeed)
            {
                score += MatchupBonus;
            }
            return true;
        }

        // ------------------------------------------------------------ (c) attack

        /// <summary>One hand card's attack drop, scored before the bot knows whether it can pay for it.</summary>
        private struct AttackOption
        {
            public int Slot;
            public UnitDefinition Card;
            public Fix Utility;
            public FixVector2 Target;
            public string Label;
        }

        private void AddAttack()
        {
            MapDefinition map = _s.Map.Definition;
            int target = PickLane(out Fix weakness);
            if (target < 0 || !NearestDeployableToStructure(target, out FixVector2 lanePoint))
            {
                return;
            }
            Fix laneDistance = DistanceToFootprint(lanePoint, target);

            // The most advanced own tank in this lane (a pending tank deploy counts at its drop point).
            bool tankPushing = false;
            FixVector2 tankPosition = lanePoint;
            Fix best = Fix.MaxValue;
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == Player && unit.IsAlive && IsTank(unit.Definition))
                {
                    Fix d = DistanceToFootprint(unit.Position, target);
                    if (d <= laneDistance + TankAheadSlack && d < best)
                    {
                        best = d;
                        tankPushing = true;
                        tankPosition = unit.Position;
                    }
                }
            }
            foreach (PendingSpawn spawn in _s.PendingSpawns)
            {
                if (spawn.Owner == Player && IsTank(spawn.Definition))
                {
                    Fix d = DistanceToFootprint(spawn.Target, target);
                    if (d <= laneDistance + TankAheadSlack && d < best)
                    {
                        best = d;
                        tankPushing = true;
                        tankPosition = spawn.Target;
                    }
                }
            }
            FixVector2 supportPoint = lanePoint;
            if (tankPushing)
            {
                FixVector2 forward = (NearestFootprintPoint(target, tankPosition) - tankPosition).Normalized;
                if (!FindDeployable(tankPosition - forward * SupportSpacing, out supportPoint))
                {
                    supportPoint = lanePoint;
                }
            }

            Fix scale = Fix.Half + _aggression;
            Fix bonus = weakness * MatchupBonus;
            Fix wait = _suddenDeath ? Fix.Zero : AttackWaitGold * (Fix.One - _aggression);
            string lane = map.Structures[target].Id;

            _attackKeep = _reserve + wait;

            // Score every card in hand first, whether or not the bot can pay for it: the plan is what it wants, not
            // what it happens to be able to afford this second.
            for (int slot = 0; slot < _me.Cards.HandSize; slot++)
            {
                if (!(_me.Cards.Hand[slot] is UnitDefinition card) || IsBlockedLeader(card))
                {
                    continue;
                }
                if (tankPushing)
                {
                    if (IsTank(card))
                    {
                        AddOption(slot, card, SecondTankUtility * scale + bonus, lanePoint, "tank again at " + lane);
                    }
                    else
                    {
                        AddOption(slot, card, SupportUtility * scale + bonus, supportPoint, "support " + card.Id + " at " + lane);
                    }
                }
                else if (IsTank(card))
                {
                    AddOption(slot, card, TankLeadUtility * scale + bonus, lanePoint, "tank " + card.Id + " at " + lane);
                }
                else
                {
                    AddOption(slot, card, (LoneLeadUtility + MatchupBonus * _aggression) * scale + bonus, lanePoint,
                        "lead " + card.Id + " at " + lane);
                }
            }

            _savingsGoal = SavingsGoal(wait);
            foreach (AttackOption option in _attackOptions)
            {
                if (CanAfford(option.Card, _reserve + wait + _savingsGoal))
                {
                    Add(ActionKind.Card, option.Utility, option.Slot, option.Target, option.Label);
                }
                else if (_savingsGoal > Fix.Zero && CanAfford(option.Card, _reserve + wait)
                    && option.Utility > _savingsFloor)
                {
                    // An attack the bot could pay for but is holding its gold back from. Nothing else may quietly
                    // take that gold unless it is worth more than the attack the bot just declined.
                    _savingsFloor = option.Utility;
                }
            }
        }

        private void AddOption(int slot, UnitDefinition card, Fix utility, FixVector2 target, string label)
        {
            _attackOptions.Add(new AttackOption { Slot = slot, Card = card, Utility = utility, Target = target, Label = label });
        }

        /// <summary>
        /// The gold to hold back for the card the plan wants (the leader, a tank, anything the bot would rather push
        /// with) when that card outranks everything it can pay for right now. Holding its whole cost means a cheaper
        /// card is only played when there is still enough left for the goal, so the bot stops trickling its gold away
        /// and actually gets its expensive cards onto the field. Zero when nothing is worth waiting for, and always
        /// zero in sudden death, where the bot is all in. Defence ignores this; an unanswered push costs more than a
        /// missed leader. A bot at the gold cap may always spend (see <see cref="CanAfford"/>), so saving can never
        /// make it bank gold it would lose.
        /// </summary>
        private Fix SavingsGoal(Fix wait)
        {
            if (_suddenDeath)
            {
                return Fix.Zero;
            }
            Fix bestAffordable = Fix.MinValue;
            int goal = -1;
            for (int i = 0; i < _attackOptions.Count; i++)
            {
                AttackOption option = _attackOptions[i];
                if (CanAfford(option.Card, _reserve + wait))
                {
                    if (option.Utility > bestAffordable)
                    {
                        bestAffordable = option.Utility;
                    }
                }
                else if (goal < 0 || option.Utility > _attackOptions[goal].Utility)
                {
                    goal = i;
                }
            }
            return goal >= 0 && _attackOptions[goal].Utility > bestAffordable
                ? Fix.FromInt(_attackOptions[goal].Card.Cost)
                : Fix.Zero;
        }

        /// <summary>
        /// The lane to push: each enemy forward tower is a lane holding that tower and the enemy Keep; the lane with the
        /// least structure hp left (as a share of its full hp) wins, ties to the lower tower index. The target is the
        /// lane's tower while it stands, else the Keep. <paramref name="weakness"/> is the share of the lane's hp gone.
        /// </summary>
        private int PickLane(out Fix weakness)
        {
            weakness = Fix.Zero;
            MapDefinition map = _s.Map.Definition;
            int keep = map.GetKeep(_enemy).Index;
            StructureState keepState = _s.Structures[keep];
            if (keepState.IsDestroyed)
            {
                return -1;
            }
            int best = -1;
            Fix bestShare = Fix.MaxValue;
            foreach (StructureDefinition tower in map.GetStructures(_enemy, StructureKind.ForwardTower))
            {
                StructureState t = _s.Structures[tower.Index];
                Fix left = keepState.Hp + (t.IsDestroyed ? Fix.Zero : t.Hp);
                Fix share = left / (keepState.MaxHp + t.MaxHp);
                if (share < bestShare)
                {
                    bestShare = share;
                    best = t.IsDestroyed ? keep : tower.Index;
                }
            }
            if (best < 0)
            {
                bestShare = keepState.Hp / keepState.MaxHp;
                best = keep;
            }
            weakness = Fix.One - bestShare;
            return best;
        }

        // ------------------------------------------------------------ (d) mines

        private void AddMines()
        {
            if (_suddenDeath || Personality.MineFocus == Fix.Zero)
            {
                return;
            }
            int slot = -1;
            UnitDefinition? cheapest = null;
            for (int i = 0; i < _me.Cards.HandSize; i++)
            {
                if (_me.Cards.Hand[i] is UnitDefinition card && card.CanCapture && !card.IsLeader
                    && (cheapest == null || card.Cost < cheapest.Cost))
                {
                    cheapest = card;
                    slot = i;
                }
            }
            if (cheapest == null || !CanAfford(cheapest, _reserve))
            {
                return;
            }
            foreach (Economy.MineState mine in _s.Mines)
            {
                if (mine.Owner == Player || HasOwnUnitNear(mine.Position, MineGuardRadius, capturersOnly: true)
                    || !FindDeployable(mine.Position, out FixVector2 target))
                {
                    continue;
                }
                AddSpending(MineUtility * Personality.MineFocus, slot, target,
                    "mine " + mine.Index + " with " + cheapest.Id);
            }
        }

        // ------------------------------------------------------------ (d2) chests

        /// <summary>
        /// A chest waiting on the bot's own side of the field is free gold on the way to the front, so the cheapest
        /// unit card in hand is dropped at the deployable cell nearest to it instead of at the lane point. It is
        /// scored as the attack it replaces plus what the chest is worth, and paid for like that attack (reserve,
        /// aggression wait and savings included), so fetching a chest never costs the bot a push and never becomes
        /// the thing it spends its savings on. Like the mine drops this only points a unit that way: units never
        /// walk to a chest on purpose, they collect one they pass, and a drop right on the spawn cell takes it as
        /// soon as the unit appears. A chest nearer the enemy Keep than the bot's own is left alone, because going
        /// for it means walking into the enemy's half. Skipped when an own unit or pending deploy is already there,
        /// when the nearest deploy point is too far from the chest for the drop to count as going for it, and in
        /// sudden death, where only structure damage wins.
        /// </summary>
        private void AddChests()
        {
            if (_suddenDeath || Personality.MineFocus == Fix.Zero)
            {
                return;
            }
            int best = -1;
            for (int i = 0; i < _attackOptions.Count; i++)
            {
                UnitDefinition card = _attackOptions[i].Card;
                if (!card.IsLeader && (best < 0 || card.Cost < _attackOptions[best].Card.Cost))
                {
                    best = i;
                }
            }
            if (best < 0 || !CanAfford(_attackOptions[best].Card, _attackKeep + _savingsGoal))
            {
                return;
            }
            AttackOption option = _attackOptions[best];
            Fix worth = ChestUtilityPerGold * _rules.ChestGold * Personality.MineFocus;
            foreach (Economy.ChestState chest in _s.Chests)
            {
                if (!chest.IsPresent || !IsOnOwnSide(chest.Position)
                    || HasOwnUnitNear(chest.Position, ChestGuardRadius, capturersOnly: false)
                    || !FindDeployable(chest.Position, out FixVector2 target)
                    || FixVector2.Distance(target, chest.Position) > ChestSendRadius
                    || FixVector2.Distance(target, option.Target) > ChestDetourRadius)
                {
                    continue;
                }
                Add(ActionKind.Card, option.Utility + worth, option.Slot, target,
                    "chest " + chest.Index + " with " + option.Card.Id);
            }
        }

        /// <summary>An own unit (on the field or about to spawn) is already this close to a point on the map.</summary>
        private bool HasOwnUnitNear(FixVector2 point, Fix radius, bool capturersOnly)
        {
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == Player && unit.IsAlive && (!capturersOnly || unit.Definition.CanCapture)
                    && FixVector2.Distance(unit.Position, point) <= radius)
                {
                    return true;
                }
            }
            foreach (PendingSpawn spawn in _s.PendingSpawns)
            {
                if (spawn.Owner == Player && (!capturersOnly || spawn.Definition.CanCapture)
                    && FixVector2.Distance(spawn.Target, point) <= radius)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ (e) spells

        private Fix SpellThreshold => SpellThresholdAtZeroUsage - Personality.SpellUsage;

        private void AddSpells()
        {
            for (int slot = 0; slot < _me.Cards.HandSize; slot++)
            {
                if (!(_me.Cards.Hand[slot] is SpellDefinition spell) || !CanAfford(spell, Fix.Zero))
                {
                    continue;
                }
                Fix factor = _rules.LevelFactor(_me.Deck.GetLevel(spell));
                int pulses = 1;
                if (spell.IsZone)
                {
                    int interval = Math.Max(1, _rules.SecondsToTicks(spell.ZoneTickSeconds));
                    pulses = (_rules.SecondsToTicks(spell.DurationSeconds) + interval - 1) / interval;
                }
                Fix unitDamage = spell.Damage * factor * Fix.FromInt(pulses);
                Fix structureDamage = spell.StructureDamage * factor * Fix.FromInt(pulses);
                if (!BestStrike(spell.Radius, unitDamage, structureDamage, spell.Targets, out FixVector2 aim, out Fix value))
                {
                    continue;
                }
                Fix cost = Fix.FromInt(spell.Cost);
                Fix need = cost * SpellThreshold;
                bool ownHalf = IsOnOwnHalf(aim);
                if (value <= need || (!ownHalf && !CanAfford(spell, _reserve)))
                {
                    continue;
                }
                string label = "cast " + spell.Id + " (value " + value + ")";
                if (ownHalf)
                {
                    Add(ActionKind.Card, SpellOwnHalfUtility + value * OwnHalfSpellValueWeight - need, slot, aim, label);
                }
                else
                {
                    AddSpending(SpellUtility + value - need, slot, aim, label);
                }
            }
        }

        /// <summary>
        /// The densest place to strike: every enemy unit the strike can hit, and every enemy structure it can hurt, is a
        /// candidate center; the value-weighted center of what a candidate hits is tried too. Value = for each enemy
        /// unit in radius its card cost share times the fraction of its hp the strike removes (at most 1); plus, when
        /// structures take damage, a big bonus per structure in sudden death (first damage wins) or a bonus for a kill.
        /// </summary>
        private bool BestStrike(Fix radius, Fix unitDamage, Fix structureDamage, TargetLayer layer, out FixVector2 aim,
            out Fix value)
        {
            FixVector2 bestAim = FixVector2.Zero;
            Fix bestValue = Fix.Zero;
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == _enemy && unit.IsAlive && TargetRules.CanHitUnit(layer, unit.IsFlying))
                {
                    Consider(unit.Position);
                }
            }
            if (TargetRules.CanHitStructures(layer) && structureDamage > Fix.Zero)
            {
                foreach (StructureState s in _s.Structures)
                {
                    if (s.Owner == _enemy && !s.IsDestroyed)
                    {
                        Consider(FootprintCenter(s.Definition.Footprint));
                    }
                }
            }
            aim = bestAim;
            value = bestValue;
            return bestValue > Fix.Zero;

            void Consider(FixVector2 center)
            {
                Fix v = StrikeValue(center, radius, unitDamage, structureDamage, layer, out FixVector2 centroid);
                if (v > bestValue)
                {
                    bestValue = v;
                    bestAim = center;
                }
                if (centroid != center && IsOnMap(centroid))
                {
                    Fix w = StrikeValue(centroid, radius, unitDamage, structureDamage, layer, out _);
                    if (w > bestValue)
                    {
                        bestValue = w;
                        bestAim = centroid;
                    }
                }
            }
        }

        private Fix StrikeValue(FixVector2 center, Fix radius, Fix unitDamage, Fix structureDamage, TargetLayer layer,
            out FixVector2 centroid)
        {
            Fix value = Fix.Zero;
            Fix sumX = Fix.Zero, sumY = Fix.Zero, weight = Fix.Zero;
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner != _enemy || !unit.IsAlive || !TargetRules.CanHitUnit(layer, unit.IsFlying)
                    || FixVector2.Distance(unit.Position, center) > radius)
                {
                    continue;
                }
                Fix v = ShareOf(unit.Definition) * Fix.Min(Fix.One, unitDamage / unit.Hp);
                value += v;
                sumX += unit.Position.X * v;
                sumY += unit.Position.Y * v;
                weight += v;
            }
            centroid = weight > Fix.Zero ? new FixVector2(sumX / weight, sumY / weight) : center;
            if (TargetRules.CanHitStructures(layer) && structureDamage > Fix.Zero)
            {
                foreach (StructureState s in _s.Structures)
                {
                    if (s.Owner != _enemy || s.IsDestroyed || DistanceToFootprint(center, s.Index) > radius)
                    {
                        continue;
                    }
                    if (_suddenDeath)
                    {
                        value += SuddenDeathStructureValue;
                    }
                    else if (structureDamage >= s.Hp)
                    {
                        value += KillStructureValue;
                    }
                }
            }
            return value;
        }

        // ------------------------------------------------------------ (f) leader ability

        private void AddAbility()
        {
            LeaderAbilityDefinition? ability = _me.Deck.Leader.Ability;
            if (ability == null || _s.Tick < _me.AbilityReadyTick || !HasLiveLeader())
            {
                return;
            }
            switch (ability.Type)
            {
                case AbilityType.Rally:
                    AddRally(ability);
                    break;
                case AbilityType.Heal:
                    AddHeal(ability);
                    break;
                case AbilityType.AreaDamage:
                    Fix amount = ability.Amount * _rules.LevelFactor(_me.Deck.LeaderLevel);
                    if (BestStrike(ability.Radius, amount, amount * ability.StructureDamageMultiplier, TargetLayer.Both,
                            out FixVector2 aim, out Fix value)
                        && value > AbilityValueThreshold * SpellThreshold && ClampToLeader(ref aim, ability.Range))
                    {
                        Add(ActionKind.Ability, value, -1, aim, ability.DisplayName + " (value " + value + ")");
                    }
                    break;
            }
        }

        /// <summary>Rally when several own units are attacking at (or closing on) an enemy structure.</summary>
        private void AddRally(LeaderAbilityDefinition ability)
        {
            foreach (StructureState s in _s.Structures)
            {
                if (s.Owner != _enemy || s.IsDestroyed)
                {
                    continue;
                }
                Fix sumX = Fix.Zero, sumY = Fix.Zero;
                int count = 0;
                foreach (Unit unit in _s.Units)
                {
                    if (unit.Owner == Player && unit.IsAlive
                        && ((unit.Target.Kind == TargetKind.Structure && unit.Target.Id == s.Index)
                            || DistanceToFootprint(unit.Position, s.Index) <= RallyGatherRadius))
                    {
                        sumX += unit.Position.X;
                        sumY += unit.Position.Y;
                        count++;
                    }
                }
                if (count < RallyMinUnits)
                {
                    continue;
                }
                var aim = new FixVector2(sumX / Fix.FromInt(count), sumY / Fix.FromInt(count));
                if (!ClampToLeader(ref aim, ability.Range))
                {
                    continue;
                }
                int inRadius = CountOwnUnitsWithin(aim, ability.Radius);
                if (inRadius >= RallyMinUnits)
                {
                    Add(ActionKind.Ability, Fix.FromInt(inRadius), -1, aim, ability.DisplayName + " at " + s.Definition.Id
                        + " (" + inRadius + " units)");
                }
            }
        }

        /// <summary>Heal where the most missing hp (capped at the heal amount per unit) is within the radius.</summary>
        private void AddHeal(LeaderAbilityDefinition ability)
        {
            Fix amount = ability.Amount * _rules.LevelFactor(_me.Deck.LeaderLevel);
            Fix bestValue = Fix.Zero;
            FixVector2 bestAim = FixVector2.Zero;
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner != Player || !unit.IsAlive || unit.Hp >= unit.MaxHp)
                {
                    continue;
                }
                FixVector2 aim = unit.Position;
                if (!ClampToLeader(ref aim, ability.Range))
                {
                    continue;
                }
                Fix missing = Fix.Zero;
                foreach (Unit other in _s.Units)
                {
                    if (other.Owner == Player && other.IsAlive && FixVector2.Distance(other.Position, aim) <= ability.Radius)
                    {
                        missing += Fix.Min(amount, other.MaxHp - other.Hp);
                    }
                }
                if (missing > bestValue)
                {
                    bestValue = missing;
                    bestAim = aim;
                }
            }
            if (bestValue >= amount * HealMinAmounts)
            {
                Add(ActionKind.Ability, bestValue / amount, -1, bestAim, ability.DisplayName + " (heals " + bestValue + ")");
            }
        }

        private int CountOwnUnitsWithin(FixVector2 center, Fix radius)
        {
            int count = 0;
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == Player && unit.IsAlive && FixVector2.Distance(unit.Position, center) <= radius)
                {
                    count++;
                }
            }
            return count;
        }

        private bool HasLiveLeader()
        {
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == Player && unit.IsAlive && unit.Definition.IsLeader)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Pulls the aim toward the nearest own leader until it is within the ability's range.</summary>
        private bool ClampToLeader(ref FixVector2 aim, Fix range)
        {
            Unit? leader = null;
            Fix best = Fix.MaxValue;
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == Player && unit.IsAlive && unit.Definition.IsLeader)
                {
                    Fix d = FixVector2.Distance(unit.Position, aim);
                    if (d < best)
                    {
                        best = d;
                        leader = unit;
                    }
                }
            }
            if (leader == null)
            {
                return false;
            }
            if (best > range)
            {
                aim = leader.Position + (aim - leader.Position) * (range * AbilityRangeMargin / best);
            }
            return FixVector2.Distance(leader.Position, aim) <= range;
        }

        // ------------------------------------------------------------ validity (mirrors the sim's rules)

        private bool CanAfford(CardDefinition card, Fix keep)
        {
            Fix cost = Fix.FromInt(card.Cost);
            return _me.Gold >= cost && _me.Gold >= Fix.Min(cost + keep, _rules.GoldCap);
        }

        private bool IsBlockedLeader(CardDefinition card) => card.IsLeader && _s.HasLeaderOnField(Player);

        private bool CanDeploy(int slot, FixVector2 target)
        {
            CardDefinition? card = _me.Cards.GetSlot(slot);
            if (card == null || _me.Gold < Fix.FromInt(card.Cost) || IsBlockedLeader(card))
            {
                return false;
            }
            return card is SpellDefinition ? IsOnMap(target) : _s.Map.IsDeployable(Player, target);
        }

        private bool CanUseAbility(FixVector2 target)
        {
            LeaderAbilityDefinition? ability = _me.Deck.Leader.Ability;
            if (ability == null || _s.Tick < _me.AbilityReadyTick)
            {
                return false;
            }
            foreach (Unit unit in _s.Units)
            {
                if (unit.Owner == Player && unit.IsAlive && unit.Definition.IsLeader
                    && FixVector2.Distance(unit.Position, target) <= ability.Range)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ geometry

        /// <summary>Tank slot, or any unit with tank-sized hp (such as a sturdy leader).</summary>
        private static bool IsTank(UnitDefinition d) => d.Slot == UnitSlot.Tank || d.Hp >= TankHp;

        /// <summary>A unit's worth: its card cost split over the card's spawn count.</summary>
        private static Fix ShareOf(UnitDefinition d) => Fix.FromInt(d.Cost) / Fix.FromInt(d.SpawnCount);

        /// <summary>Card cost share scaled by the hp the unit has left.</summary>
        private static Fix ValueOf(Unit unit) => ShareOf(unit.Definition) * unit.Hp / unit.MaxHp;

        /// <summary>Closer to the bot's own Keep than to the enemy Keep.</summary>
        private bool IsOnOwnHalf(FixVector2 p) =>
            FixVector2.Distance(p, _ownKeepCenter) < FixVector2.Distance(p, _enemyKeepCenter);

        /// <summary>Not farther from the bot's own Keep than from the enemy Keep, so a point exactly on the center
        /// line counts for both players.</summary>
        private bool IsOnOwnSide(FixVector2 p) =>
            FixVector2.Distance(p, _ownKeepCenter) <= FixVector2.Distance(p, _enemyKeepCenter);

        private bool IsOnMap(FixVector2 p)
        {
            MapDefinition map = _s.Map.Definition;
            return p.X >= Fix.Zero && p.Y >= Fix.Zero && p.X < Fix.FromInt(map.Width) * map.CellSize
                && p.Y < Fix.FromInt(map.Height) * map.CellSize;
        }

        private int NearestOwnStructure(FixVector2 p, out Fix distance)
        {
            distance = Fix.MaxValue;
            int best = -1;
            foreach (StructureState s in _s.Structures)
            {
                if (s.Owner != Player || s.IsDestroyed)
                {
                    continue;
                }
                Fix d = DistanceToFootprint(p, s.Index);
                if (d < distance)
                {
                    distance = d;
                    best = s.Index;
                }
            }
            return best;
        }

        private Fix DistanceToFootprint(FixVector2 p, int structure) =>
            UnitMovement.DistanceToFootprint(_s.Map.Grid, p, structure);

        private FixVector2 FootprintCenter(CellRect r)
        {
            Fix cell = _s.Map.Definition.CellSize;
            return new FixVector2((Fix.FromInt(r.X) + Fix.FromInt(r.Width) * Fix.Half) * cell,
                (Fix.FromInt(r.Y) + Fix.FromInt(r.Height) * Fix.Half) * cell);
        }

        private FixVector2 NearestFootprintPoint(int structure, FixVector2 p)
        {
            CellRect r = _s.Map.Definition.Structures[structure].Footprint;
            Fix cell = _s.Map.Definition.CellSize;
            return new FixVector2(
                Fix.Clamp(p.X, Fix.FromInt(r.X) * cell, Fix.FromInt(r.XEnd) * cell),
                Fix.Clamp(p.Y, Fix.FromInt(r.Y) * cell, Fix.FromInt(r.YEnd) * cell));
        }

        /// <summary>Rebuilds the list of deployable cell centers when walkability or the unlocked zones change.</summary>
        private void RefreshDeployable()
        {
            Grid grid = _s.Map.Grid;
            int unlocks = _s.Map.GetUnlockedZones(Player).Count;
            if (grid.WalkabilityVersion == _deployableVersion && unlocks == _deployableUnlocks)
            {
                return;
            }
            _deployableVersion = grid.WalkabilityVersion;
            _deployableUnlocks = unlocks;
            _deployable.Clear();
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    FixVector2 center = grid.CellToWorld(x, y);
                    if (_s.Map.IsDeployable(Player, center))
                    {
                        _deployable.Add(center);
                    }
                }
            }
        }

        /// <summary>The wanted point if deployable, else the nearest deployable cell center (ties: lower row, then left).</summary>
        private bool FindDeployable(FixVector2 wanted, out FixVector2 result)
        {
            if (_s.Map.IsDeployable(Player, wanted))
            {
                result = wanted;
                return true;
            }
            result = wanted;
            Fix best = Fix.MaxValue;
            foreach (FixVector2 center in _deployable)
            {
                Fix d = FixVector2.Distance(center, wanted);
                if (d < best)
                {
                    best = d;
                    result = center;
                }
            }
            return best != Fix.MaxValue;
        }

        /// <summary>
        /// The deployable cell center nearest to a structure's footprint, preferring cells out of reach of every
        /// standing enemy structure (with a margin) so a wave does not spawn under fire. Ties: lower row, then left.
        /// </summary>
        private bool NearestDeployableToStructure(int structure, out FixVector2 result)
        {
            result = FixVector2.Zero;
            Fix best = Fix.MaxValue;
            bool bestSafe = false;
            foreach (FixVector2 center in _deployable)
            {
                bool safe = !IsUnderEnemyFire(center);
                if (bestSafe && !safe)
                {
                    continue;
                }
                Fix d = DistanceToFootprint(center, structure);
                if (d < best || (safe && !bestSafe))
                {
                    best = d;
                    result = center;
                    bestSafe = safe;
                }
            }
            return best != Fix.MaxValue;
        }

        private bool IsUnderEnemyFire(FixVector2 p)
        {
            foreach (StructureState s in _s.Structures)
            {
                if (s.Owner == _enemy && !s.IsDestroyed && DistanceToFootprint(p, s.Index) <= s.Stats.Range + SafeMargin)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
