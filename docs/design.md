# NovaFaction — Game Design v0.1 (Sept 2026)

## Vision
Real-time unit-drop battler (Clash Royale / Warcraft Rumble genre). Commercial free-to-play.
Android first; iOS and PC later. Solo developer using Claude Code.
Themed factions released over time as content packs; fantasy faction first.

## Match rules
- 1v1, human or bot. 3:00 clock.
- Win: enemy Keep destroyed = instant win. Otherwise higher damage score at the clock.
  Exact tie -> sudden death. No draws.
- Sudden death (decided Sept 2026, implemented Sept 2026): lasts 60s; gold income is doubled
  (rules.json suddenDeathIncomeMultiplier); the first player to deal any structure damage wins.
  If it expires with no damage dealt, tie-breaks apply in this order:
  1. more enemy structures destroyed;
  2. higher HP on your own weakest structure (a destroyed structure counts as 0 HP);
  3. more gold collected from mines and chests (base income does not count);
  4. a coin flip from the match seed.
- Score = HP removed from enemy structures + a destruction bonus per structure killed.
- Structures per side: Keep (base) + 2 forward towers. Forward towers attack.
  Destroying a forward tower extends the attacker's deploy zone onto that side.
- Resource: Gold (display name is faction data). Steady base income plus two map sources:
  gold mines (neutral, capturable, extra income, capped) and gold chests (spawn on the
  field, collected by walking a unit over them). Target: active player earns ~1/3 more than a turtle.
- Stored gold caps at 10. Unit costs 1-7.
- Deck of 8 = 1 leader + 7 cards. Hand of 4, next card visible. Cycle order shuffled from
  the match seed, then loops.
- Leader deploys like a unit; carries the faction passive and an active ability on cooldown.
- 1s spawn delay on deploy. Deploy zones are map data (own side + around held structures).

## Maps
- A map is a data file: terrain grid, structure positions, deploy zones, mine/chest spawn
  points, decoration set. Layouts vary per map.
- Launch PvP pool (3): two-lane symmetric; open field with chokepoints; three-lane with center mine.
- Fairness rule: ranked draws only from a free rotating pool. Purchased/unlocked maps appear
  only in PvE, friendly matches, and a casual host-picks mode.
- PvE campaign maps may be asymmetric (defend a pass, siege a fortress).

## Factions (themes)
- Theme = faction (own unit roster) + arenas + UI skin + music, shipped as a content pack.
- Every faction fills the same ~10 archetype slots: tank, bruiser, swarm, ranged, flyer,
  siege, support, spell, building, leader. Slot = job; faction = flavor + one twist mechanic.
- Unit gold cost derives from one shared cost formula (HP, damage, speed, range, utility budget).
- Launch: fantasy only, 14-16 units incl. 2 leaders. Second faction = first major post-launch
  release. Never two factions in development at once.

## PvE campaign
- Linear chapters, light story via text panels. Launch: 5 chapters x 7 missions on ~12 map
  layouts; one boss per chapter with a unique mechanic. Chapter 1 is the tutorial: missions 1-5
  each introduce one mechanic (deploy, gold cap, chests, mines, forward towers, damage score).
- Win conditions are mission data: destroy target, survive the clock, most damage, escort.
- Enemies = the PvP bot brain with a preset deck and a personality (aggressive, turtle, swarm).
  Difficulty knobs: enemy unit levels, income multiplier, reaction delay, decision quality.
  Bosses add scripted waves.
- 3 stars per mission: win; win with Keep above 50% HP; mission-specific challenge.
  Star totals advance a chapter reward track with fixed rewards (main unit-earning path).
- Heroic tier per chapter after clearing it: leveled enemies + a modifier, better shard rewards.
- Pacing: start with 6 units, unlock one every 2-3 missions through chapters 1-2 (~12 by chapter 3).

## Progression and economy
- Currencies: Gold (in-match only), Coins (earned meta), Gems (premium).
- Units earned via campaign, ranked season rewards, season pass; or bought outright with gems.
  Every unit is earnable.
- Unit levels cost coins + shards (duplicates). Shards from play; gem purchase allowed under a
  daily cap. ~5-7% stat gain per level.
- Matchmaking: rating + deck power (average unit level). Level cap per league.
- Season pass: 30-day cycle, free and paid tracks. Home of faction/map releases.
- Daily quests (3) and weekly quests for coins, shards, a trickle of gems.
- Cosmetics within a faction: unit skins, base skins, victory effects.
- No loot boxes or random purchases. No ads. No energy gates.
- Ranked matchmaking falls back to human-like bots when the queue is empty.

## Social
- None at launch. Ranked leaderboard only (top 100 + own rank). Data model must allow
  friends and clans later without migration.

## Store and compliance
- Server owns the economy: rewards, purchases, levels and balances computed server-side.
- Purchase receipts verified server-side with Google/Apple before granting.
- Teen rating; not child-directed (avoid COPPA scope).

## Technical architecture
- client/: Unity 6.3 LTS, URP, Android first. Renders sim state; no game rules in Unity code.
- sim/: netstandard2.1 C# library. Deterministic: fixed-point math, seeded RNG, fixed 20 ticks/s,
  flow-field pathfinding on a grid. Headless bot-vs-bot harness; per-tick state hash for
  cross-platform determinism checks. No Unity references.
- Sim numerics (sim/NovaFaction.Sim):
  - Fix = Q48.16 fixed point (long raw, 1.0 = 65536). + - * / saturate at MaxValue/MinValue
    instead of wrapping; * and / use exact 128-bit intermediates and round to nearest, ties away
    from zero (symmetric for negatives). Divide by zero throws. Round() is also ties-away-from-zero.
  - Balance data is written as plain decimals ("1.5", "-0.25"; no exponent, no leading '+',
    no ".5" / "5.") and parsed with integer math, rounded to the nearest 1/65536.
    Fix.ToString() gives the shortest decimal that parses back to the same value.
  - Fix.ToFloat() is view-only for the client. A reflection test fails the build if any other
    sim member uses float or double.
  - FixVector2.Length/Normalized use exact 128-bit sums of squares, so they stay accurate for tiny
    and huge vectors; LengthSquared/Dot saturate beyond about +/-8,000,000 per component.
  - SimRandom = PCG32 (XSH-RR, reference stream 54), state is a single ulong (GetState/SetState)
    for state hashes and replays. NextInt is unbiased (rejection sampling). Chance always consumes
    exactly one draw. Changing the algorithm breaks saved replays; a test pins reference output.
- Sim match loop (sim/NovaFaction.Sim):
  - content/rules.json holds match-wide rules (tick rate, clock, sudden death length and income
    multiplier, gold income/start/cap, spawn delay, hand and deck size, unit separation, stopped push
    and spawn spacing, aggro radius, melee crowd penalty). Every key is required; unknown keys are
    errors. JSON has no comments, so values that are still guesses are listed by name in
    "tuningPlaceholders". Current placeholders: base income 0.35 gold/s (about Clash Royale's pace),
    5 starting gold, the four unit movement values (see "Units, decks and movement") and the two
    targeting values (see "Combat and match resolution").
    suddenDeathIncomeMultiplier (2) is design data, not a placeholder; base income times it must not
    exceed the gold cap. unitStoppedPushFactor must be 0..1.
    The spawn delay must be a whole number of ticks. handSize must be less than deckSize.
  - SimJson: strict RFC 8259 reader, no dependencies. Numbers stay as text until read as int/long or
    Fix (exponents are valid JSON but rejected as Fix). Errors carry file name, line and column.
    Duplicate keys are errors; a leading byte-order mark is allowed; nesting max 64.
    The sim takes JSON text, not file paths, so the client can load it however Unity needs.
  - Command = tick, player (0/1), per-player sequence number, type (None, DeployCard,
    LeaderAbility), hand slot (-1 when unused), target. Canonical order: tick, player, sequence.
    Malformed commands (bad player/slot/type, wrong tick, duplicate player+sequence) make Tick()
    throw with state unchanged; they are input-layer bugs, not gameplay. Game-rule rejections
    (empty hand slot, not enough gold, target not deployable) are deterministic no-ops that only
    increase the player's IgnoredDeploys counter, which is in the state hash.
  - Simulation.Tick(commands) order: validate, record in the CommandLog, apply commands in
    canonical order, create units of zero-delay deploys, combat and movement (see "Combat and match
    resolution"), resolve Keep kills and sudden-death damage, accrue income (multiplied in sudden
    death), advance tick and clock; then, unless the match just ended, create units whose spawn delay
    is over and handle clock expiry.
    Tick N's commands must be stamped N (State.Tick before the call). A 3:00 match is 3600 ticks.
  - A match is built from a MatchSetup (rules, map, structure stats, player 0's deck, player 1's deck)
    plus the seed:
    new Simulation(setup, seed); Simulation.Replay(setup, seed, log). Each deck carries its own
    faction roster, so the two players may later bring different factions.
  - Income is exact: each tick adds income/ticksPerSecond with the sub-raw remainder carried per
    player, so a player gains exactly the per-second income every whole second. At the cap the
    carry is discarded (a capped player banks nothing).
  - Clock expiry: see "Combat and match resolution".
  - State hash: 64-bit FNV-1a over little-endian bytes, starting with a hash format version.
    Covers tick, RNG state, clock, phase, winner, end reason, tie-break rule, and per player: gold
    raw, income carry, score, gold collected, command count, ignored deploys, the deck's unit-data
    content hash, hand slots and draw queue. Then the map, the structures file's content hash, every
    structure's combat state (index order: index, hp, attack cooldown, target unit id), the next unit
    id, every unit (id order: id, owner, card id, position, hp, state, objective, target kind and id,
    attack cooldown), every pending spawn (deploy order), the next projectile id and every projectile
    (id order: id, owner, position, target, aim point, speed per tick, damage, splash radius, target
    layer). Hash format version is 4.
    New state implements IStateHashable and appends count-then-items in id order.
    A test pins the hash of a scripted full match on the small test map with fixed inline rules and
    structure stats (it includes kills, projectiles and a Keep kill); change it only on purpose.
  - Replay = rules + seed + CommandLog (Simulation.Replay). The log is in memory only for now.
  - Replay file format (decided Sept 2026, not implemented yet): compact, versioned binary.
    Header: replay format version, sim version, content version, rules version, seed, map id (plus
    the map's content hash), and both players' decks: each deck's faction, card ids and the unit
    level of every card. Body: the command log.
    Decided Sept 2026: the header must carry the map id, both decks (card ids plus unit levels) and
    the rules version. rules.json has no version field yet; add one when replays are built.
    A JSON export of the same data exists for debugging only; the binary file is authoritative
    (it is what the server verifies).
- Sim map layer (sim/NovaFaction.Sim/Map):
  - Map files: content/maps/<id>.json with formatVersion (1), id (a-z 0-9 _ -), cellSize (world units
    per cell, 1/16..64), grid, structures, deployZones. Unknown keys are errors.
  - grid: equal-length strings, first string = top row. Legend: '.' ground, '#' blocked, 'K' Keep,
    'T' forward tower, 'M' gold mine, 'C' chest spawn, '0'/'1' player spawn-side marker.
    M, C, 0, 1 are markers on open ground (walkable). Max 256x256.
  - Coordinates: cell (0,0) is bottom-left; y grows toward player 1. World origin is the
    bottom-left corner of cell (0,0); cell (x,y) covers [x, x+1) * cellSize on each axis.
    Player 0 owns the bottom, player 1 the top, but owners are written explicitly in the file.
  - structures: list of { id, kind "keep"|"tower", owner 0|1, footprint {x,y,width,height} },
    towers also have unlocksDeployZone (a rectangle on the owner's side that the other player may
    deploy into once the tower is destroyed). A structure's number in the sim is its list position.
    The K/T letters must match the declared footprints exactly (both ways); this double entry catches
    typos. deployZones: list of { player, x, y, width, height }; at least one per player.
  - Validation: rectangular grid; known characters; exactly 1 Keep + 2 towers per player;
    footprints in bounds, non-overlapping, and drawn with the right letter; spawn/mine/chest
    markers never under a footprint; all rectangles in bounds; at least one '0' and one '1' marker,
    each inside its own player's base deploy zone; every structure, mine and chest spawn reachable
    from every spawn marker. Errors name the file, line and cell.
  - Map content hash: FNV-1a over the parsed data (not the file bytes), so line endings and
    whitespace, which git may change per platform, do not matter. Folded into the state hash along
    with the map id, which structures are destroyed, and each player's unlocked zones.
    Hash format version is now 2.
  - Grid: walkable = in bounds, not '#', and not under a standing structure. Destroying a structure
    makes its footprint walkable and bumps a walkability version. WorldToCell is exact floor division.
  - FlowField (one per target structure, cached, rebuilt when the walkability version changes):
    Dijkstra from the target's cells over walkable cells, 8 neighbors, straight cost 1, diagonal
    cost sqrt(2) = 92682/65536. No corner cutting: a diagonal needs both side cells open. The target's
    own cells count as open. Distance = path cost from cell center to nearest target cell center,
    times cellSize; Fix.MaxValue when unreachable. Direction = one of 8 unit vectors along a shortest
    path, zero inside the target or when unreachable. Ties: best aligned with the line to the
    target center, then the one to the left of that line, then a fixed order. The first two rules
    are unchanged by a 180-degree rotation, so on a symmetric map both players route identically
    (a test checks this on twolane).
  - Deploy: allowed on a walkable cell inside one of the player's base zones or unlocked zones.
    Unlocked zones are listed in tower order, not destruction order. Destroying a Keep unlocks nothing.
  - Simulation now takes the map: new Simulation(rules, map, seed); Replay(rules, map, seed, log).
  - content/maps/twolane.json: 18x32, 180-degree symmetric (also left-right symmetric apart from
    the mines). Keeps 4x3, towers 2x2, a 4-row blocked river with two 4-wide lane gaps,
    one mine at the inner edge of each gap (one on each side of the center line), two chest spawns
    per lane, base deploy zones = each player's 13 rows nearest their Keep, tower unlocks = a 9x6
    block in front of the fallen tower's half of the field.
- Units, decks and movement (sim/NovaFaction.Sim/Content, Cards, Units):
  - Unit files: content/factions/<faction>/units.json with formatVersion (1), faction (id), units.
    Each unit: id (a-z 0-9 _ -, unique in the file), displayName, slot (tank, bruiser, swarm, ranged,
    flyer, siege, support, spell, building, leader), cost (whole number 1-7), hp (> 0), damage (>= 0),
    attackIntervalSeconds (> 0), range (> 0), moveSpeed (>= 0), targets (ground, air, both),
    targetPriority (any = enemy units and structures, structuresOnly), isFlying, spawnCount (1-25),
    isLeader (must be true exactly when slot is leader). Optional: projectileSpeed (> 0; present =
    ranged, absent = melee), splashRadius (> 0; absent = single target), "placeholder": true marks
    numbers that are guesses. Unknown keys are errors. The file's content
    hash (from parsed data) is part of the state hash.
  - content/factions/fantasy/units.json: 8 placeholder units, one per requested job: stone_golem
    (tank), knight (bruiser), goblin_pack (swarm of 4), elf_archer (ranged), griffin (flyer),
    catapult (siege), fire_spirit (spell stand-in, a fast 1 HP unit until real spells exist) and
    warlord (leader). Every number is a placeholder. Ranged: elf_archer (projectile 10/s) and catapult
    (6/s). Splash: catapult (1.25) and fire_spirit (1.5, melee). Structures only: stone_golem and
    catapult.
  - Range is measured from the unit's center to the nearest point of the target's footprint, so
    melee units use a small positive range (0.5).
  - Deck: exactly deckSize (8) different card ids from one roster, exactly one of them a leader
    (no duplicate cards). Stored sorted by id, so the order a player lists cards never matters.
    Unit levels are not part of the deck yet; they arrive with progression and belong in the replay
    header.
  - Hand: at match start player 0's deck is shuffled with the match RNG, then player 1's. The first
    handSize cards are the hand (slots 0-3), the rest the queue; the front of the queue is the
    visible next card. Playing a slot puts the next card into that slot and the played card at the
    back of the queue. Slots are never empty under current rules; the sim supports an empty slot
    (deploys from it are rejected) for future rules.
  - Deploy: valid when the slot holds a card, gold >= cost, and the target is deployable for that
    player now (base or unlocked zones, walkable cell). Commands apply in canonical order, so a
    second deploy in the same tick sees the gold the first one spent. A valid deploy pays, cycles
    the hand and queues a pending spawn for deploy tick + delay ticks. With a delay of D > 0 ticks the
    units are in the state once State.Tick reaches deploy tick + D, in the Spawning state, unmoved.
    With D = 0 they appear during the deploy tick and move on it. The spawn delay must be a whole
    number of ticks that Q48.16 can represent exactly (0.25 s works, 0.05 s does not).
  - Spawn pattern: square spiral in steps of unitSpawnSpacing (0.5): center, front, left, right,
    back, then the diagonals, then the next ring. Rotated 180 degrees for player 1 so both players'
    formations face the enemy the same way. A position on a blocked cell moves to the nearest
    walkable cell center within 3 cells (ties: lower row, then left), else to the deploy target.
  - Units: id (from 1, never reused), owner, card, position, hp, state (Spawning, Moving, Holding,
    Attacking), objective (structure index or none), target (enemy unit id, structure index or none)
    and attack cooldown. Stored in id order. Holding now means "nothing it can attack" (e.g. no enemy
    structure left); a unit in range of its target is Attacking.
    The client reads MatchState.Units, Structures, Projectiles, PendingSpawns, Winner/EndReason and
    each player's Cards (Hand, NextCard, Queue); only the sim can change them.
  - Movement runs in two passes so update order cannot matter: every unit decides state, target,
    objective and velocity from the positions at the start of the tick, then moving units step
    (targeting details in "Combat and match resolution").
    - Spawning units act (and may attack) on their first tick.
    - A unit that is not locked onto a target re-picks its objective every tick: the nearest standing
      enemy structure by flow-field path distance (ground) or by straight-line distance to the
      footprint (flyers). Ties go to the lower structure index. No structure left: Holding with no
      objective. When any structure is destroyed, every unit re-picks its objective that same tick.
    - Within range of the target (before or after the step): Attacking. An arriving unit stops on the
      tick it arrives and attacks from the next one.
    - Ground direction: bilinear blend of the flow directions of the 4 cells around the unit,
      leaving out blocked cells and cells whose path cost differs from the unit's own cell by more
      than 2 (the other side of a wall); falls back to the unit's own cell direction.
    - Chasing an enemy unit (ground): the flow field toward the target's cell (built on demand, cached
      per cell until walkability changes), blended the same way; straight at the target once it is in
      the same or a neighboring cell. Flyers fly straight at the target.
    - Flyers fly straight at the footprint center, ignore terrain and are kept inside the map.
    - Separation (soft collision): each friendly unit of the same layer (ground or air) closer than
      unitSeparationDistance (0.6) pushes by (distance short / separation distance) along the line
      between them. Pushes from moving neighbors and from stopped (Attacking or Holding at tick
      start) neighbors are summed separately, each capped at 1. The stopped sum is scaled by
      unitStoppedPushFactor (0.3, placeholder), so it is always slower than any unit and cannot hold
      a unit short of its target; moving units may therefore overlap stopped friends a little.
      The stopped sum also adds a sideways slide of the same size as the (unweakened) sum,
      perpendicular to the unit's heading, toward the side it is already offset to (left when exactly
      head-on; left stays left under the map's 180-degree rotation, so both players behave alike).
      Being sideways, the slide never slows the unit. The total is scaled by
      unitSeparationPushPerSecond (1.5). Two units on exactly the same point split along X, lower id
      to the left. Stopped units are never pushed. Enemies do not push each other.
    - Speed: velocity = direction * moveSpeed + push, divided by the tick rate per step.
    - Ground step: take the full step if it lands on a walkable cell without cutting a blocked
      corner; otherwise the pure flow step (always open); otherwise stay. MatchSetup rejects content
      where moveSpeed + push * (2 + unitStoppedPushFactor) would exceed half a cell per tick, which keeps
      these checks sound.
- Combat and match resolution (sim/NovaFaction.Sim/Combat):
  - content/structures.json: formatVersion (1) and one entry per kind, "keep" and "tower" (the map file
    names): hp, damage, attackIntervalSeconds, range, targets, projectileSpeed, destructionBonus, optional
    "placeholder". All current numbers are placeholders: Keep 4000 HP, 90 damage every 1 s, range 6,
    bonus 1000; tower 2500 HP, 80 damage every 0.8 s, range 7, bonus 500; both target both layers and
    shoot at 10 units/s. The Keep attacks from the start (no Clash Royale style activation yet).
    Its content hash is part of the state hash.
  - Structures track HP (MatchState.Structures). At 0 HP a structure is destroyed: footprint walkable,
    flow fields rebuild, a tower's unlock zone goes to the attacker, every unit re-picks its objective.
  - Who can hit what: Ground attackers hit ground units and structures; Air attackers hit only flyers
    (and never structures); Both hits everything. Flyers are untouchable by Ground-only attackers.
  - Unit targeting, each tick, from start-of-tick state:
    - A unit keeps an enemy unit as its target while it lives, can be hit, and stays within the scan
      radius; a ground attacker also drops it when it is out of range and standing somewhere the
      attacker cannot walk to (e.g. over the river). A structure target is kept only while it stands
      and is in range (locked while attacking); otherwise the unit looks again every tick.
    - Scan radius = max(aggroRadius, the unit's range); aggroRadius is 5.5 (placeholder), so a unit
      never ignores an enemy it could already hit.
    - Scan: the nearest enemy it may attack within the radius, by straight-line distance (to the unit's
      center, or to the nearest point of a footprint). targetPriority any: enemy units and standing
      enemy structures; structuresOnly: structures only. Ties: units before structures, then lower id
      or index. No hit: the objective structure (see movement). An Air-only unit with no target still
      walks to its objective and Holds there.
    - Spreading melee: a melee unit scores an enemy unit as distance + meleeTargetCrowdPenalty (1,
      placeholder) per friendly unit already targeting it. Units decide in id order and each pick or
      drop updates the count at once, so friends deciding later in the same tick see it. Counts are
      per player, so the order never favors either player.
  - Range: unit to unit is center to center; unit to structure and structure to unit is to the nearest
    point of the footprint.
  - Attacks: attack cooldowns count down every tick in every state; a unit or structure whose target is
    in range attacks when its cooldown is 0, then waits attackIntervalSeconds (rounded to the nearest
    whole tick, at least 1). A fresh unit already in range attacks on its first tick. Melee damage
    lands the same tick. Ranged units and all structures fire a Projectile (from the unit's position or
    the footprint center) that first moves on the next tick, homes on the target at projectileSpeed,
    and always hits when it gets there (arrival = remaining distance <= one tick's travel). Aim point:
    the target unit's position (updated each tick while it lives) or the nearest footprint point to
    where it was fired from. If the target is dead or destroyed, the projectile flies on to the last
    aim point and fizzles there with no damage. Damage, splash and target layer are copied into the
    projectile when fired.
  - Structures shoot the nearest enemy unit in range they can hit (distance from the footprint, ties
    to the lower id) and keep that target while it stays in range.
  - Splash (optional splashRadius): damages every enemy unit the attacker could hit whose center is
    within the radius of the impact point, and every enemy structure whose footprint is. Priority does
    not matter for splash (a structuresOnly catapult's splash still hurts units). No friendly fire.
  - Tick order inside combat: cooldowns; unit decisions; structure decisions; projectiles in flight
    move and arrive; units then structures attack; all hits apply in that order (projectile id, unit
    id, structure index); moving units that are still alive step; units at 0 HP are removed; targets
    pointing at removed units or destroyed structures are cleared. All hits in a tick are
    simultaneous: a unit killed this tick still gets its attack.
  - Score: HP actually removed from enemy structures (a hit is capped by the HP left) plus the
    structure's destructionBonus when it falls. Killing units scores nothing.
  - Resolution (MatchState.Winner 0/1 or -1, EndReason, TieBreakRule):
    - A Keep destroyed: Ended at once, the attacker wins (KeepDestroyed), whatever the score. If both
      Keeps fall on the same tick: the higher score wins (Score), else the tie-break list.
    - Regulation clock at zero: higher score wins (Score). Equal: SuddenDeath with a fresh
      suddenDeathSeconds clock and income times suddenDeathIncomeMultiplier (a 0 s sudden death goes
      straight to the tie-break list).
    - Sudden death: the first tick with any structure HP removed ends the match (FirstDamage). If both
      players removed structure HP that tick, the one who removed more wins; exactly equal goes to the
      tie-break list. Unit-on-unit damage does not count.
    - Sudden death clock at zero: the tie-break list (TieBreak, TieBreakRule says which rule decided):
      1. more enemy structures destroyed; 2. higher HP on your own weakest structure, where a destroyed
      structure counts as 0 HP (as decided above; so if both players lost a structure this rule is
      level); 3. more gold collected from mines and chests (PlayerState.GoldCollected, always 0 until
      mines and chests exist); 4. a coin flip: SimRandom.NextInt(0, 2) from the match RNG, the only
      draw combat makes.
- server/: ASP.NET Core (C#). Accounts, economy, matchmaking, input relay, match verification by
  re-running the sim. PostgreSQL. Runs on the Windows desktop for LAN testing; cloud container later.
- content/: JSON data for units, factions, maps, missions. Art in Addressables bundles per theme.
- Netcode: custom deterministic lockstep with input relay. Photon Quantum is the fallback;
  decision at end of M1.

## Milestones
- M0 Setup (Sept-Oct 2026)
- M1 Sim + bot, headless (Oct 2026-Jan 2027)
- M2 Vertical slice on phone: 1 map, 8 units, PvE vs bot (Feb-Apr 2027)
- M3 Accounts, relay, LAN PvP, verification (May-Jul 2027)
- M4 Full fantasy roster, 3 PvP maps, campaign ch.1-3, economy, ranked (Aug 2027-Jan 2028)
- M5 IAP, polish, analytics, cloud, closed test (Feb-Apr 2028)
- M6 Ch.4-5, Heroic, balance, store listing, Android launch (May-Jul 2028); iOS ~2 months later; PC after

## Open items
- Studio name and Android package identifier.
- Fantasy roster: the 16 units and 2 leaders.
- Income, cost and match-length numbers (tune in the headless harness).
- Replay implementation (header decided above): add a rules version field to rules.json.
- Unit levels: where they come from and how they scale stats (~5-7% per level) in the sim.
- Enemy units do not block or push each other (decided to leave as is with combat; revisit if fights
  look wrong on the phone).
- Real spells: fire_spirit stands in for the spell slot as a unit (it keeps attacking; it does not
  die on its first hit as a Clash Royale fire spirit would).
- Keep activation: the Keep shoots from the start. Clash Royale only wakes the king tower once it is
  hit or a tower falls; decide when tuning.
- Combat numbers (structure HP/damage/range, aggro radius, stopped push, crowd penalty) are
  placeholders; with the shipped numbers towers win most fights against a trickle of units. Tune in
  the headless harness.
- Tie-break rule 2 wording: the Sept 2026 combat request said "weakest standing structure"; the design
  decision above (a destroyed structure counts as 0 HP) was kept. Confirm.
- Crowding at structures (fixed Sept 2026 with the weak stopped push and sideways slide): a busy
  scripted battle test requires that every living unit is Attacking or Holding at the end.
