using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NovaFaction.Sim.Commands;

namespace NovaFaction.Sim.Replays
{
    /// <summary>
    /// Writes replays in the binary format (authoritative) and as JSON (debugging only).
    /// <para>
    /// Binary layout, version 1. Integers are little-endian; "varint" is unsigned LEB128, "svarint" is a zigzag varint,
    /// "string" is a varint byte length followed by UTF-8.
    /// </para>
    /// <list type="bullet">
    /// <item>Header: magic "NFRP" (4 bytes), formatVersion (u16), simVersion (varint), contentVersion (u64), rulesVersion
    /// (varint), map id (string), map content hash (u64), seed (u64); then for each player: faction (string), card
    /// count (varint), each card's id (string) and level (varint) in deck order, structure level (varint), bot flag
    /// (byte 0/1) and, for a bot, its personality id (string).</item>
    /// <item>Body: command count (varint), then each command in canonical order: tick minus the previous command's tick
    /// (varint), player (byte), sequence (varint), type (byte), hand slot + 1 (varint), target x and y raw Q48.16
    /// (svarint each).</item>
    /// <item>Footer: final tick (varint), final state hash (u64). Nothing may follow.</item>
    /// </list>
    /// </summary>
    public static class ReplayWriter
    {
        internal static readonly byte[] Magic = { (byte)'N', (byte)'F', (byte)'R', (byte)'P' };

        public static byte[] Write(Replay replay)
        {
            using (var stream = new MemoryStream())
            {
                Write(replay, stream);
                return stream.ToArray();
            }
        }

        public static void Write(Replay replay, Stream stream)
        {
            if (replay == null)
            {
                throw new ArgumentNullException(nameof(replay));
            }
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            var w = new ByteWriter(stream);
            w.Bytes(Magic);
            w.U16(Replay.FormatVersion);
            w.VarInt((uint)replay.SimVersion);
            w.U64(replay.ContentVersion);
            w.VarInt((uint)replay.RulesVersion);
            w.String(replay.MapId);
            w.U64(replay.MapContentHash);
            w.U64(replay.Seed);
            foreach (ReplayPlayer player in replay.Players)
            {
                w.String(player.Faction);
                w.VarInt((uint)player.CardIds.Count);
                for (int i = 0; i < player.CardIds.Count; i++)
                {
                    w.String(player.CardIds[i]);
                    w.VarInt((uint)player.CardLevels[i]);
                }
                w.VarInt((uint)player.StructureLevel);
                w.Byte(player.IsBot ? (byte)1 : (byte)0);
                if (player.BotId != null)
                {
                    w.String(player.BotId);
                }
            }

            CommandLog log = replay.Log;
            w.VarInt((uint)log.CommandCount);
            int previousTick = 0;
            for (int tick = 0; tick < log.TickCount; tick++)
            {
                foreach (Command c in log.GetCommands(tick))
                {
                    w.VarInt((uint)(c.Tick - previousTick));
                    previousTick = c.Tick;
                    w.Byte((byte)c.Player);
                    w.VarInt((uint)c.Sequence);
                    w.Byte((byte)c.Type);
                    w.VarInt((uint)(c.HandSlot + 1));
                    w.SVarInt(c.Target.X.Raw);
                    w.SVarInt(c.Target.Y.Raw);
                }
            }

            w.VarInt((uint)replay.FinalTick);
            w.U64(replay.FinalHash);
        }

        /// <summary>
        /// The replay as indented JSON, for reading and diffing. Not a save format: nothing reads it back. 64-bit values
        /// are hex strings (JSON numbers lose precision past 2^53); positions are exact decimals.
        /// </summary>
        public static string ToJson(Replay replay)
        {
            if (replay == null)
            {
                throw new ArgumentNullException(nameof(replay));
            }
            var sb = new StringBuilder();
            sb.Append("{\n");
            Field(sb, 1, "formatVersion", Num(Replay.FormatVersion));
            Field(sb, 1, "simVersion", Num(replay.SimVersion));
            Field(sb, 1, "contentVersion", Quote(Replay.Hex(replay.ContentVersion)));
            Field(sb, 1, "rulesVersion", Num(replay.RulesVersion));
            Field(sb, 1, "mapId", Quote(replay.MapId));
            Field(sb, 1, "mapContentHash", Quote(Replay.Hex(replay.MapContentHash)));
            Field(sb, 1, "seed", Num(replay.Seed));
            sb.Append("  \"players\": [\n");
            for (int p = 0; p < replay.Players.Count; p++)
            {
                ReplayPlayer player = replay.Players[p];
                var cards = new List<string>();
                for (int i = 0; i < player.CardIds.Count; i++)
                {
                    cards.Add("{ \"id\": " + Quote(player.CardIds[i]) + ", \"level\": " + Num(player.CardLevels[i]) + " }");
                }
                sb.Append("    {\n");
                Field(sb, 3, "player", Num(p));
                Field(sb, 3, "faction", Quote(player.Faction));
                Field(sb, 3, "cards", "[\n        " + string.Join(",\n        ", cards) + "\n      ]");
                Field(sb, 3, "structureLevel", Num(player.StructureLevel));
                Field(sb, 3, "bot", player.BotId == null ? "null" : Quote(player.BotId), last: true);
                sb.Append(p == replay.Players.Count - 1 ? "    }\n" : "    },\n");
            }
            sb.Append("  ],\n");
            var commands = new List<string>();
            for (int tick = 0; tick < replay.Log.TickCount; tick++)
            {
                foreach (Command c in replay.Log.GetCommands(tick))
                {
                    commands.Add("{ \"tick\": " + Num(c.Tick) + ", \"player\": " + Num(c.Player) + ", \"sequence\": "
                        + Num(c.Sequence) + ", \"type\": " + Quote(c.Type.ToString()) + ", \"handSlot\": " + Num(c.HandSlot)
                        + ", \"x\": " + c.Target.X + ", \"y\": " + c.Target.Y + " }");
                }
            }
            Field(sb, 1, "commandCount", Num(commands.Count));
            Field(sb, 1, "commands", commands.Count == 0 ? "[]" : "[\n    " + string.Join(",\n    ", commands) + "\n  ]");
            Field(sb, 1, "finalTick", Num(replay.FinalTick));
            Field(sb, 1, "finalHash", Quote(Replay.Hex(replay.FinalHash)), last: true);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Num(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        private static void Field(StringBuilder sb, int depth, string name, string value, bool last = false)
        {
            sb.Append(' ', depth * 2).Append('"').Append(name).Append("\": ").Append(value).Append(last ? "\n" : ",\n");
        }

        private static string Quote(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private sealed class ByteWriter
        {
            private readonly Stream _stream;

            public ByteWriter(Stream stream) => _stream = stream;

            public void Byte(byte value) => _stream.WriteByte(value);

            public void Bytes(byte[] value) => _stream.Write(value, 0, value.Length);

            public void U16(int value)
            {
                Byte((byte)value);
                Byte((byte)(value >> 8));
            }

            public void U64(ulong value)
            {
                for (int shift = 0; shift < 64; shift += 8)
                {
                    Byte((byte)(value >> shift));
                }
            }

            public void VarInt(ulong value)
            {
                while (value >= 0x80)
                {
                    Byte((byte)(value | 0x80));
                    value >>= 7;
                }
                Byte((byte)value);
            }

            public void SVarInt(long value) => VarInt(unchecked((ulong)((value << 1) ^ (value >> 63))));

            public void String(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                VarInt((uint)bytes.Length);
                Bytes(bytes);
            }
        }
    }
}
