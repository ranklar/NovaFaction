using System;
using System.Diagnostics;
using NovaFaction.Sim;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using UnityEngine;

namespace NovaFaction.Client
{
    /// <summary>
    /// Proves the sim DLL and the content files work inside Unity, in the editor and on the phone.
    ///
    /// On Start it plays one headless balanced-vs-balanced match on twolane with seed 7 and shows the result.
    /// That is the same match as the harness command
    ///     dotnet run -c Release --project tools\NovaFaction.Harness -- run --seed 7 --p0 balanced --p1 balanced --map twolane
    /// so the final state hash shown here must equal the harness's. A different hash means the sim is not
    /// deterministic across platforms, which is the thing this test exists to catch.
    ///
    /// Drop this component on any GameObject in the scene; it needs nothing else and draws with OnGUI.
    /// </summary>
    public sealed class SimSmokeTest : MonoBehaviour
    {
        /// <summary>The match to play. These match the harness defaults, so the hashes are comparable.</summary>
        public ulong Seed = 7;
        public string MapId = "twolane";
        public string Bot0 = "balanced";
        public string Bot1 = "balanced";

        /// <summary>The harness's default fantasy deck: all seven units plus Fireball.</summary>
        private static readonly string[] DefaultDeck =
        {
            "stone_golem", "knight", "goblin_pack", "elf_archer", "griffin", "catapult", "warlord", "fireball",
        };

        private string _report;
        private string _error;
        private GUIStyle _style;

        private void Start()
        {
            try
            {
                _report = RunMatch();
            }
            catch (Exception e)
            {
                _error = e.GetType().Name + ": " + e.Message + "\n\n" + e.StackTrace;
                UnityEngine.Debug.LogException(e);
            }
        }

        private string RunMatch()
        {
            var timer = Stopwatch.StartNew();
            ContentLibrary content = ContentLoader.Load();
            long contentMs = timer.ElapsedMilliseconds;

            MapDefinition map = content.GetMap(MapId);
            Deck deck = Deck.Create(FactionOf(content, DefaultDeck[0]), DefaultDeck, content.Rules.DeckSize);
            MatchSetup setup = new MatchSetup(content.Rules, map, content.Structures, deck, deck)
                .WithBot(0, content.GetBot(Bot0))
                .WithBot(1, content.GetBot(Bot1));

            timer.Restart();
            MatchResult result = HeadlessMatch.Run(setup, Seed);
            long matchMs = timer.ElapsedMilliseconds;

            MatchState state = result.Simulation.State;
            string tieBreak = result.TieBreakRule == TieBreakRule.None
                ? ""
                : " (" + result.TieBreakRule + ")";
            return "NovaFaction sim smoke test\n"
                + "sim version " + SimVersion.Current + ", rules version " + content.Rules.RulesVersion + "\n"
                + "seed " + Seed + ", " + MapId + ", " + Bot0 + " vs " + Bot1 + "\n"
                + "\n"
                + "winner      P" + result.Winner + "\n"
                + "end reason  " + result.EndReason + tieBreak + "\n"
                + "score       " + state.GetPlayer(0).Score + " - " + state.GetPlayer(1).Score + "\n"
                + "final tick  " + result.Ticks + " (" + (result.Ticks / content.Rules.TicksPerSecond) + " s)\n"
                + "state hash  0x" + result.FinalHash.ToString("X16") + "\n"
                + "\n"
                + "content     " + contentMs + " ms\n"
                + "match       " + matchMs + " ms\n"
                + "platform    " + Application.platform + "\n"
                + "unity       " + Application.unityVersion;
        }

        /// <summary>The faction that has this card, the way the harness picks a deck's faction.</summary>
        private static CardCatalog FactionOf(ContentLibrary content, string cardId)
        {
            foreach (CardCatalog catalog in content.Factions)
            {
                if (catalog.TryGet(cardId, out _))
                {
                    return catalog;
                }
            }
            throw new InvalidOperationException("No faction has the card \"" + cardId + "\".");
        }

        private void OnGUI()
        {
            if (_style == null)
            {
                // Scale with the screen so the text is readable on a phone and in a small editor Game view.
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(18, Mathf.RoundToInt(Screen.height * 0.030f)),
                    wordWrap = true,
                };
                _style.normal.textColor = Color.white;
            }

            _style.normal.textColor = _error != null ? Color.red : Color.white;
            float margin = Screen.width * 0.04f;
            var area = new Rect(margin, margin, Screen.width - (2 * margin), Screen.height - (2 * margin));
            GUI.Label(area, _error ?? _report ?? "Running...", _style);
        }
    }
}
