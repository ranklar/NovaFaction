using System;
using System.Collections.Generic;
using System.Text;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Replays
{
    /// <summary>
    /// Reads the binary replay format (layout in <see cref="ReplayWriter"/>). Every problem is a
    /// <see cref="ReplayException"/> with a readable message; corrupt data never causes huge allocations.
    /// </summary>
    public static class ReplayReader
    {
        private const int MaxStringBytes = 1024;
        private const int MaxCards = 64;
        // Longer than any match the rules allow (1000 ticks/s for 3600 s of regulation plus 3600 s of sudden death).
        private const int MaxFinalTick = 7_200_000;

        /// <summary>
        /// Reads a replay and checks that it can be re-run here: the format version, the sim version, and that the
        /// loaded content matches what it was recorded with (rules version, map, content version).
        /// </summary>
        public static Replay Read(byte[] data, ContentLibrary content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }
            Replay replay = Read(data);
            if (replay.SimVersion != SimVersion.Current)
            {
                throw new ReplayException(Replay.SimVersionMessage(replay.SimVersion));
            }
            replay.CreateSetup(content); // throws on a content mismatch
            return replay;
        }

        /// <summary>
        /// Reads a replay for inspection (for example <see cref="ReplayWriter.ToJson"/>), checking only the file itself
        /// and its format version, not whether this build and content can re-run it.
        /// </summary>
        public static Replay Read(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            var r = new ByteReader(data);
            for (int i = 0; i < ReplayWriter.Magic.Length; i++)
            {
                if (data.Length <= i || data[i] != ReplayWriter.Magic[i])
                {
                    throw new ReplayException("This is not a NovaFaction replay file (it does not start with \"NFRP\").");
                }
            }
            r.Skip(ReplayWriter.Magic.Length);
            int formatVersion = r.U16();
            if (formatVersion != Replay.FormatVersion)
            {
                throw new ReplayException("Replay format version " + formatVersion + " is not supported; this build reads "
                    + "version " + Replay.FormatVersion + ".");
            }
            int simVersion = r.Int("sim version");
            ulong contentVersion = r.U64();
            int rulesVersion = r.Int("rules version");
            string mapId = r.String("map id");
            ulong mapHash = r.U64();
            ulong seed = r.U64();
            var players = new ReplayPlayer[Command.PlayerCount];
            for (int p = 0; p < players.Length; p++)
            {
                string faction = r.String("faction");
                int count = r.Int("card count", MaxCards);
                var ids = new string[count];
                var levels = new int[count];
                for (int i = 0; i < count; i++)
                {
                    ids[i] = r.String("card id");
                    levels[i] = r.Int("card level");
                }
                int structureLevel = r.Int("structure level");
                byte isBot = r.Byte();
                if (isBot > 1)
                {
                    throw r.Error("the bot flag must be 0 or 1 but is " + isBot + ".");
                }
                string? botId = isBot == 1 ? r.String("bot id") : null;
                players[p] = new ReplayPlayer(faction, ids, levels, structureLevel, botId);
            }

            // Each command takes at least 7 bytes, which bounds the count by the data left.
            int commandCount = r.Int("command count", r.Remaining / 7);
            var commands = new Command[commandCount];
            long tick = 0;
            for (int i = 0; i < commandCount; i++)
            {
                tick += r.Int("tick step");
                if (tick > int.MaxValue)
                {
                    throw r.Error("command tick out of range.");
                }
                int player = r.Byte();
                int sequence = r.Int("sequence");
                int type = r.Byte();
                int handSlot = r.Int("hand slot") - 1;
                long x = r.SVarInt();
                long y = r.SVarInt();
                if (player >= Command.PlayerCount || type > (int)CommandType.LeaderAbility)
                {
                    throw r.Error("command " + i + " has player " + player + " and type " + type + ".");
                }
                commands[i] = new Command((int)tick, player, sequence, (CommandType)type, handSlot,
                    new FixVector2(Fix.FromRaw(x), Fix.FromRaw(y)));
            }

            int finalTick = r.Int("final tick", MaxFinalTick);
            ulong finalHash = r.U64();
            if (r.Remaining != 0)
            {
                throw r.Error(r.Remaining + " unexpected bytes after the footer.");
            }
            if (commandCount > 0 && commands[commandCount - 1].Tick >= finalTick)
            {
                throw new ReplayException("A command is stamped for tick " + commands[commandCount - 1].Tick
                    + " but the replay ends at tick " + finalTick + ".");
            }

            var log = new CommandLog();
            var tickCommands = new List<Command>();
            int next = 0;
            try
            {
                for (int t = 0; t < finalTick; t++)
                {
                    tickCommands.Clear();
                    while (next < commandCount && commands[next].Tick == t)
                    {
                        tickCommands.Add(commands[next++]);
                    }
                    log.Record(t, tickCommands);
                }
            }
            catch (ArgumentException e)
            {
                throw new ReplayException("The replay's command log is invalid: " + e.Message, e);
            }
            return new Replay(simVersion, contentVersion, rulesVersion, mapId, mapHash, seed, players, log, finalTick, finalHash);
        }

        private sealed class ByteReader
        {
            private readonly byte[] _data;
            private int _position;

            public ByteReader(byte[] data) => _data = data;

            public int Remaining => _data.Length - _position;

            public ReplayException Error(string message) =>
                new ReplayException("Replay file is corrupt at byte " + _position + ": " + message);

            public void Skip(int count) => _position += count;

            public byte Byte()
            {
                if (_position >= _data.Length)
                {
                    throw Error("the file ends too early.");
                }
                return _data[_position++];
            }

            public int U16() => Byte() | (Byte() << 8);

            public ulong U64()
            {
                ulong value = 0;
                for (int shift = 0; shift < 64; shift += 8)
                {
                    value |= (ulong)Byte() << shift;
                }
                return value;
            }

            public ulong VarInt()
            {
                ulong value = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    byte b = Byte();
                    value |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0)
                    {
                        return value;
                    }
                }
                throw Error("a number is too long.");
            }

            public long SVarInt()
            {
                ulong raw = VarInt();
                return unchecked((long)(raw >> 1) ^ -(long)(raw & 1));
            }

            /// <summary>A varint that must fit in 0..max.</summary>
            public int Int(string what, int max = int.MaxValue)
            {
                ulong value = VarInt();
                if (value > (ulong)max)
                {
                    throw Error(what + " " + value + " is out of range (at most " + max + ").");
                }
                return (int)value;
            }

            public string String(string what)
            {
                int length = Int(what + " length", MaxStringBytes);
                if (length > Remaining)
                {
                    throw Error("the file ends inside the " + what + ".");
                }
                string value = Encoding.UTF8.GetString(_data, _position, length);
                _position += length;
                return value;
            }
        }
    }
}
