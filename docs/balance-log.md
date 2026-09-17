# NovaFaction — balance log

One entry per tuning iteration: what changed, why, and the numbers it produced. The targets live in
docs/design.md under "Balance targets". Every value here is still a placeholder.

## How the numbers are measured

Release build of the harness, on twolane, always with the same seeds so two iterations differ only by what changed:

```powershell
dotnet build -c Release tools\NovaFaction.Harness\NovaFaction.Harness.csproj
dotnet run -c Release --no-build --project tools\NovaFaction.Harness -- roundrobin --matches-per-pair 100 --map twolane --out tools\out\<iteration>
dotnet run -c Release --no-build --project tools\NovaFaction.Harness -- batch --matches 200 --p0 balanced --p1 balanced --map twolane --out tools\out\<iteration>
```

The round robin gives the per-personality win rates and the active-versus-turtle income ratios (seeds 1..100 for each
of the 16 ordered pairings). The 200-match balanced mirror gives the Keep-kill rate, the tower-kill rate, the
sudden-death rate and the per-card efficiency table.

| Target | Where it is read |
| --- | --- |
| Turtle at or below 45%, nobody above 60% or below 40% | round robin, "Overall" table |
| Active-versus-turtle income 1.25-1.40 | round robin, "Active vs turtle income" table |
| Keep-kill rate 20-35% | mirror batch, "Keep-kill rate" |
| A forward tower down in 60%+ of matches | mirror batch, "Tower-kill rate" |
| Sudden death under 10% | mirror batch, "Sudden death" |
| No card over 2x the median card | mirror batch, "Card efficiency" |

## Baseline (before this session)

The shipped placeholders, with no bot or balance change yet. This is what the rest of the log is measured against.

Round robin, 100 matches per ordered pairing (1600 matches):

| bot | played | win rate | gold/match |
| --- | --- | --- | --- |
| turtle | 800 | 69.6% | 74.3 |
| balanced | 800 | 58.2% | 74.3 |
| swarm | 800 | 48.9% | 74.1 |
| aggressive | 800 | 23.2% | 73.1 |

Overall rates: Keep kill 0.0%, tower kill 0.2%, sudden death 3.8%.
Active-versus-turtle income, mean over the six mixed pairings: **1.001** (aggressive 0.99, balanced 1.01, swarm 1.00).

Balanced mirror, 200 matches:

| stat | value |
| --- | --- |
| win rate | P0 59.0% / P1 41.0% |
| Keep-kill rate | 0.0% |
| tower-kill rate | 0.5% |
| sudden death | 6.0% |
| match length | 180.2 s +/- 0.8 |
| score margin (absolute) | 267 +/- 291 |
| gold per side | 74.4 (63.1 base, 8.5 mine, 2.9 chest) |
| chests collected per side | 3.8 of 20 |

Card efficiency, both sides pooled (median 4.7 structure damage per gold, 0.163 kills per gold):

| card | gold | dmg/gold | x median | kills/gold | x median |
| --- | --- | --- | --- | --- | --- |
| catapult | 350 | 50.7 | **10.88** | 0.000 | 0.00 |
| fireball | 2944 | 0.7 | 0.15 | 0.325 | 2.00 |
| elf_archer | 4800 | 6.0 | 1.30 | 0.263 | 1.62 |
| goblin_pack | 4770 | 5.0 | 1.07 | 0.240 | 1.48 |
| griffin | 5736 | 4.9 | 1.04 | 0.107 | 0.66 |
| warlord | 2436 | 0.2 | 0.05 | 0.167 | 1.03 |
| knight | 6004 | 0.8 | 0.18 | 0.158 | 0.97 |
| stone_golem | 2820 | 4.5 | 0.96 | 0.000 | 0.00 |

Targets met at the baseline: sudden death (3.8% / 6.0%). Everything else is missed, most of them badly: turtle wins
69.6%, aggressive wins 23.2%, the income ratio is 1.00 instead of 1.25-1.40, and essentially no structure ever falls,
so the Keep-kill and tower-kill targets are at zero. The catapult is a huge outlier on structure damage per gold, but
it is deployed only 0.17 times per match, so that number rests on 70 deploys over 200 matches.

## Step 1 — bot fixes (no balance number changed)

Three changes to the bot brain, all in sim/NovaFaction.Sim/Bots/BotController.cs. No content file and no balance
number was touched, so the difference below is the bot alone. SimVersion is now 2.

**(a) Saving for the card the plan wants.** The bot used to spend every coin it could afford, so its 6-cost leader
never came out: by the time it had 6 gold it had already bought two cheap cards. `AddAttack` now scores every unit
card in hand whether or not the bot can pay for it. If the best of those is one it cannot afford and it outranks
everything it can, the bot holds that card's whole cost back (`SavingsGoal`) and only plays something cheaper when
there is still enough left for the goal. A bot at the gold cap may always spend, so saving never makes it bank gold
it would lose, and defence is never held back — an unanswered push costs more than a missed leader.

The first version of this pushed the held gold straight into whatever cheap action was left (a mine drop, a chest),
which is the opposite of saving. So a mine or chest drop, or an attacking spell, now has to beat the best attack the
bot just declined (`_savingsFloor`) before it may break into the savings.

**(b) Chests.** The bot had no chest behaviour at all: on twolane both of a player's own chests sit one row behind
its own front line, and the timelines showed the enemy's units walking over them while the owner's units marched
the other way. A chest drop is now modelled as the attack it replaces, routed via the chest: the cheapest non-leader
card in hand, at the deployable cell nearest the chest, scored as that card's attack utility plus what the chest is
worth (3 x chestGold x mineFocus), and paid for exactly like that attack. Only chests no farther from the bot's own
Keep than from the enemy's are fetched, only when no own unit or pending deploy is already within 4 of the chest,
and only when the drop point is within 6 of the lane point the bot is pushing — otherwise the bot splits its army
across the map for 0.75 gold. Skipped in sudden death, where only structure damage wins.

**(c) Other bugs.** None found that were clearly bugs rather than design weaknesses. What was checked: the bot never
issues a command the sim rejects (an existing test already pins this); the lane, deploy-point and threat-grouping
code is correct as written; command sequence numbers are per tick and never collide. The 59%/41% P0 win rate in the
baseline mirror is seed variance, not a side bias: the same 400 matches from seed 5000 give 51.7%/48.2%.

Two real weaknesses were found and deliberately left alone, because fixing either is a design change rather than a
bug fix:
- Every non-tank card scores the same attack utility, so which card the bot pushes with comes down to hand slot
  order and cost. The catapult is the visible symptom: it is deployed 0.17 times per match while the knight goes
  3.8 times, because at the same cost of 5 the Stone Golem always outranks it on the tank bonus and it can never
  defend (structures only).
- The bot has no model of tower fire, so it cannot tell a card that will reach a structure from one that dies on
  the way. That is the whole reason attacking loses to defending here.

### Numbers after step 1

Round robin, 100 matches per ordered pairing:

| bot | baseline | after step 1 | gold/match (baseline -> now) |
| --- | --- | --- | --- |
| turtle | 69.6% | **61.9%** | 74.3 -> 74.2 |
| balanced | 58.2% | **54.9%** | 74.3 -> 74.6 |
| swarm | 48.9% | **51.7%** | 74.1 -> 75.1 |
| aggressive | 23.2% | **31.5%** | 73.1 -> 74.1 |

Overall rates: Keep kill 0.0% (unchanged), tower kill 0.5% (0.2%), sudden death 3.5% (3.8%).
Active-versus-turtle income: **1.013** (1.001).

Balanced mirror, 200 matches: Keep kill 0.0%, tower kill 0.0%, sudden death 4.5%, mean gold 75.0/74.2 per side
(63.1 base, 8.6/7.7 mine, 3.4 chest), chests collected 4.5 per side (3.8 at the baseline).

The leader is the clearest win. In 100 aggressive-versus-aggressive matches the warlord was deployed **0 times** at
the baseline and **1.25-1.46 times per match** now, and War Cry went from never to 0.36-0.43 uses per match. The
balanced bot against a do-nothing opponent still destroys the Keep in 100% of 20 matches (95% at the baseline).

No target is met by the bot fixes alone, but the spread of personalities is much tighter (the gap between the best and the
worst personality fell from 46.4 to 30.4 points) and the economy target moved
the right way. Everything else needs balance numbers.

## Step 2 — balance numbers

One lever per iteration, measured the same way every time. No bot code changed in any of these; SimVersion stays at
2. Every iteration's round robin and mirror batch are the same 100 and 200 seeds, so two rows differ only by the
lever. Every value below is still marked as a placeholder in its content file.

### Iteration 1 — structure damage

Keep damage 90 -> 65, tower damage 80 -> 55 (content/structures.json). The open item said "towers win most fights
against a trickle of units", and the baseline bore that out: units died on the way in and almost nothing reached a
structure.

Mirror score per side 430 -> 678, tower-kill rate 0.0% -> 2.0%, overall tower kills 0.2% -> 7.2%. Win rates barely
moved (turtle 61.9 -> 68.9%): weaker towers help the defender's units survive and push too, so this lever is close to
neutral between personalities. Nothing was met yet.

### Iteration 2 — structure hp

Keep hp 4000 -> 2600, tower hp 2500 -> 1500.

Tower-kill rate 2.0% -> 13.5% in the mirror and 7.2% -> 24.2% overall; Keep kills still 0.0% in the mirror. Win
rates again unchanged to within a point, which is useful: structure hp and damage move how decisive a match is
without moving who wins it.

### Iteration 3 — chest spawn points

content/maps/twolane.json: the four chest spawns moved from (3,11), (14,11), (3,20), (14,20) - one row behind each
player's own front line - into the two river lane gaps, at (2,14), (15,14), (2,17), (15,17). They stay
180-degree symmetric, two on each player's side of the center line, and now sit outside both deploy zones, so a chest
can only be taken by sending a unit forward.

This is the change that turned the economy around. **Before it the turtle collected 2.4 times as much chest gold as
an active bot** (4.46 against 1.89 per match): the chests were in everybody's back yard, and the turtle's defenders
walked over its own while the attacker's units marched the other way. After it the ratio is 1.39 the other way.
Map-gold ratio (active over turtle) 1.08 -> 1.52, overall income ratio 1.017 -> 1.060, turtle 69.0 -> 64.2%.
The cost is that chests in the river are contested, so far fewer are collected: 4.96 -> 2.04 per side.

### Iteration 4 — mine income and chest gold

mineIncomePerSecond 0.05 -> 0.09, mineIncomeCap 0.1 -> 0.18, chestGold 0.75 -> 2.5 (rulesVersion 1 -> 2).
With the chests moved, the map-gold pool was worth having; this made it worth fighting for.

The biggest single step of the session. **Turtle 64.2% -> 43.8%** (target met), income ratio 1.060 -> 1.156,
Keep kills 0.0% -> 13.5% and tower kills 16.0% -> 63.5% in the mirror, all from the extra gold buying more units.
The asymmetry rose on its own too (map-gold ratio 1.52 -> 1.77): more gold means more units means more forward
presence means more map control, which is the loop the design intends.

### Iteration 5 — card outliers

goblin_pack cost 3 -> 4 and catapult damage 250 -> 110 (content/factions/fantasy/units.json), the two cards the
efficiency table flagged.

- goblin_pack was 2.12x the median card on structure damage per gold and 1.66x on kills per gold: four units with
  240 damage per second between them for 3 gold is 80 damage per second per gold, against a knight's 29. At 4 gold
  it lands at 1.28x and 1.26x.
- catapult was 5.4x the median on structure damage per gold. One 250-damage shot is worth about ten times what a
  knight achieves for the same gold. At 110 damage it sits at 1.49x.

Aggressive 39.0 -> 41.8% and turtle stayed at 44.6%, so **the whole win-rate target was met here**. Keep kills fell
to 9.0% and tower kills to 57.5%, because the nerfs took real damage out of the game.

### Iteration 6 — base income rate

goldBaseIncomePerSecond 0.35 -> 0.2 (rulesVersion 2 -> 3). This was taken earlier than the suggested order for a
reason worth recording: **the income target is arithmetically unreachable while base income is 0.35/s**, whatever
the map gold is worth. With base income B and map gold A for an active player and T for a turtle, the target
(B + A) / (B + T) >= 1.25 needs T <= 0.4 B at the measured asymmetry of A = 1.9 T. At B = 63 that means the turtle
must earn more than 25 gold from the map and the active player nearly 50, which no setting of the eight map-gold
values produces. Base income is the denominator the target is fighting.

Income ratio 1.167 -> 1.220 and **every card fell below the 2x outlier limit** (the catapult to 1.70x), because a
smaller economy means fewer deploys and less spread between cards. Keep kills fell to 6.5% with the smaller economy.

### Iteration 7 — structure hp and damage again

Keep 2600 -> 1700 hp and 65 -> 55 damage, tower 1500 -> 1000 hp and 55 -> 48 damage. The smaller economy of
iteration 6 had taken the Keep-kill rate down to 6.5%, so the structures had to come down with it.

**Keep kills 6.5% -> 24.0% and tower kills 63.5% -> 85.0%**, both in target. Turtle drifted back up to 48.4% and
income fell to 1.176, because matches now end early and less map gold accrues.

### Iteration 8 — chest gold

chestGold 2.5 -> 6.5 first (rulesVersion 3 -> 4), then swept down the same lever because 6.5 overshot badly. Chests
are the most asymmetric income in the game (active bots take 2.1-2.9 times the turtle's chest gold, against 1.4-1.5
for mines), so chest gold is the strongest lever on the income ratio - but it is also gold, so it drives the
Keep-kill rate at the same time. The sweep, all other values fixed:

| chestGold | income ratio | mirror Keep kills | turtle win | spread (worst-best) |
| --- | --- | --- | --- | --- |
| 2.5 (iteration 7) | 1.176 | 24.0% | 48.4% | 39.9 - 58.1% |
| **3.0 (kept)** | **1.204** | **34.0%** | **42.2%** | **42.2 - 54.8%** |
| 3.5 | 1.203 | 37.0% | 41.2% | 41.2 - 54.2% |
| 4.5 | 1.239 | 45.0% | 41.0% | 41.0 - 55.0% |
| 6.5 | 1.299 | 57.0% | 36.5% | 36.5 - 57.9% |

chestGold 3 is the only value in the sweep that keeps the Keep-kill rate inside 20-35%. Nothing on this lever
reaches the income target without pushing Keep kills far out of band: at 6.5 the income target is met (1.299) and
the Keep-kill rate is 57% with the turtle down at 36.5%. The two targets pull against each other on this lever, and
holding five of them beat holding one.

## Final state

Every number changed this session, and what it was before:

| file | value | was | now |
| --- | --- | --- | --- |
| content/structures.json | Keep hp | 4000 | **1700** |
| content/structures.json | Keep damage | 90 | **55** |
| content/structures.json | tower hp | 2500 | **1000** |
| content/structures.json | tower damage | 80 | **48** |
| content/rules.json | goldBaseIncomePerSecond | 0.35 | **0.2** |
| content/rules.json | mineIncomePerSecond | 0.05 | **0.09** |
| content/rules.json | mineIncomeCap | 0.1 | **0.18** |
| content/rules.json | chestGold | 0.75 | **3** |
| content/rules.json | rulesVersion | 1 | **4** |
| content/maps/twolane.json | chest spawns | (3,11) (14,11) (3,20) (14,20) | **(2,14) (15,14) (2,17) (15,17)** |
| content/factions/fantasy/units.json | goblin_pack cost | 3 | **4** |
| content/factions/fantasy/units.json | catapult damage | 250 | **110** |

Unchanged: every structure's range, attack interval, projectile speed and destruction bonus; every other rules value
(gold cap, starting gold, spawn delay, hand and deck size, separation, aggro, crowd penalty, mine capture radius and
seconds, give-up and retry times, chest spawn timing and collect radius, level values); every other unit and spell
number; every bot personality file; the map's terrain, structures, deploy zones and mine positions.

Round robin, 100 matches per ordered pairing (1600 matches), baseline against final:

| bot | baseline | after bot fixes | final | gold/match (baseline -> final) |
| --- | --- | --- | --- | --- |
| swarm | 48.9% | 51.7% | **54.8%** | 74.1 -> 50.4 |
| aggressive | 23.2% | 31.5% | **51.6%** | 73.1 -> 49.6 |
| balanced | 58.2% | 54.9% | **51.4%** | 74.3 -> 50.5 |
| turtle | 69.6% | 61.9% | **42.2%** | 74.3 -> 48.5 |

Balanced mirror, 200 matches, baseline against final:

| stat | baseline | final | target |
| --- | --- | --- | --- |
| Keep-kill rate | 0.0% | **34.0%** | 20-35% |
| tower-kill rate | 0.5% | **87.5%** | 60%+ |
| sudden death | 6.0% | **1.0%** | under 10% |
| match length | 180.2 s | 165.1 s | - |
| gold per side | 74.4 | 51.8 | - |
| worst card ratio | 10.88x (catapult) | **1.49x (catapult)** | under 2x |

Targets met: **five of six**.

| target | result | met |
| --- | --- | --- |
| turtle at or below 45% | 42.2% | yes |
| nobody above 60% or below 40% | 42.2% to 54.8% | yes |
| active-versus-turtle income 1.25-1.40 | 1.204 | **no** |
| Keep-kill rate 20-35% in mirrors | 34.0% | yes |
| a forward tower down in 60%+ of mirrors | 87.5% | yes |
| sudden death under 10% | 1.0% | yes |
| no card above 2x the median | 1.49x worst | yes |

The income ratio is the one miss, at 1.204 against a floor of 1.25. It is not a matter of one more nudge: the
arithmetic in iteration 6 and the sweep in iteration 8 both say the same thing, that the ratio and the Keep-kill
rate are driven by the same gold and pull against each other. Every pairing an aggressive or swarm bot plays
against the turtle is already inside the band (1.33, 1.26, 1.23); it is the balanced bot's pairings (1.20, 1.07)
that pull the mean down, because the balanced personality is not much more active than the turtle at holding the
map.

### What to try next

1. Raise chest gold to about 4.5 (income ratio 1.239) and put roughly 20% back onto the structures to hold the
   Keep-kill rate in band. That is the two-lever move iteration 8 could not make on its own, and it is the most
   likely way to land all six.
2. Give the balanced personality more appetite for the map (its mineFocus is 0.4 against swarm's 0.7). Personality
   files were out of scope this session; they are the obvious next lever for the income ratio, since the mean is
   dragged down by balanced's pairings alone.
3. Teach the bot to value cards by what they will actually do. Every non-tank card scores the same attack utility,
   so the card it pushes with comes down to hand slot order and cost, and it has no model of tower fire, so it
   cannot tell a card that will reach a structure from one that dies on the way. This is the largest remaining
   source of noise in every per-card number in this log.
4. The catapult is still nearly a dead card (660 gold spent per 200 matches against the knight's 3764). Its damage
   was cut to fix an efficiency outlier that rested on very few deploys; with a bot that valued cards properly it
   might deserve some of that back.
