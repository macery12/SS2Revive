using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SS2ReviveData
{
    public sealed class ItemSetDefinition
    {
        public string SetId;
        public string Name;
        public readonly List<string> Items = new List<string>();
    }

    public sealed class ItemDefinition
    {
        public string ItemId;
        public string SetId;
        public int Rarity;
        public string Name;
    }

    public sealed class RewardTrackEntry
    {
        public int Level;
        public long CumulativeXp;

        /// <summary>Null past the end of the cosmetic track - the last levels award nothing.</summary>
        public string SetId;
    }

    public sealed class ExperienceRules
    {
        public int PlayLevel = 25;
        public int WinLevel = 25;
        public int RateLevel = 25;
    }

    /// <summary>
    /// The item, item-set and reward-track tables, read straight out of the installed game.
    ///
    /// None of this was ever on Bossa's server. <c>InventoryService</c> resolves an item GUID to an
    /// actual hat through <c>_global.itemDefinitions</c>, which it loads from
    /// <c>StreamingAssets/Content/Data/Inventory.dat</c>; the wire protocol only ever carries the
    /// GUIDs. So a serverless build does not need the catalogue extracted and shipped - it can read
    /// the same two files the client reads, at startup, from the player's own installation.
    ///
    /// That is the single biggest simplification of going local: the old flow needed a Node script
    /// run by hand to produce a catalogue.json that then had to stay in step with the install.
    /// </summary>
    public sealed class GameCatalogue
    {
        public string SeasonId = "1";
        public readonly ExperienceRules Experience = new ExperienceRules();
        public readonly List<string> AssumeUnlockedItemSets = new List<string>();
        public readonly List<ItemSetDefinition> ItemSets = new List<ItemSetDefinition>();
        public readonly List<ItemDefinition> Items = new List<ItemDefinition>();
        public readonly List<RewardTrackEntry> RewardTrack = new List<RewardTrackEntry>();

        private readonly Dictionary<string, ItemSetDefinition> _setsById =
            new Dictionary<string, ItemSetDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool HasItemSet(string setId) =>
            setId != null && _setsById.ContainsKey(setId);

        public ItemSetDefinition GetItemSet(string setId)
        {
            if (setId == null) return null;
            return _setsById.TryGetValue(setId, out var set) ? set : null;
        }

        public bool HasItem(string itemId) => itemId != null && _itemIds.Contains(itemId);

        /// <summary>
        /// Locates <c>StreamingAssets/Content/Data</c> given the Unity <c>Application.dataPath</c>
        /// (the <c>*_Data</c> folder), or a whole install root. Returns null when neither shape
        /// leads anywhere with an Inventory.dat in it.
        /// </summary>
        public static string FindContentDirectory(string dataPathOrInstallRoot)
        {
            if (string.IsNullOrEmpty(dataPathOrInstallRoot)) return null;

            var direct = Path.Combine(dataPathOrInstallRoot,
                Path.Combine("StreamingAssets", Path.Combine("Content", "Data")));
            if (File.Exists(Path.Combine(direct, "Inventory.dat"))) return direct;

            // The _Data folder is named after the executable, so glob rather than hardcode.
            try
            {
                foreach (var candidate in Directory.GetDirectories(dataPathOrInstallRoot, "*_Data"))
                {
                    var nested = Path.Combine(candidate,
                        Path.Combine("StreamingAssets", Path.Combine("Content", "Data")));
                    if (File.Exists(Path.Combine(nested, "Inventory.dat"))) return nested;
                }
            }
            catch
            {
                // An unreadable directory is just a miss.
            }

            return null;
        }

        /// <summary>
        /// Reads both files. Throws only on a corrupt Inventory.dat; a missing or malformed
        /// ProgressionConfig.json leaves the reward track empty, which degrades to "no reward-track
        /// unlocks" rather than to a wrong catalogue.
        /// </summary>
        public static GameCatalogue Load(string contentDirectory)
        {
            var catalogue = new GameCatalogue();

            catalogue.ReadInventoryDat(File.ReadAllBytes(Path.Combine(contentDirectory, "Inventory.dat")));

            var configFile = Path.Combine(contentDirectory, "ProgressionConfig.json");
            if (File.Exists(configFile))
                catalogue.ReadProgressionConfig(File.ReadAllText(configFile, Encoding.UTF8));

            return catalogue;
        }

        /// <summary>
        /// Layout, read off <c>InventoryService.OnFileLoaded</c>:
        ///
        ///   uint32 count, Guid[count]                                 -> assumeUnlockedItemSets
        ///   uint32 count, { Guid setId,  string name }[count]         -> itemSetDefinitions
        ///   uint32 count, { Guid itemId, Guid setId, int32 rarity,
        ///                   string name }[count]                     -> itemDefinitions
        ///
        /// Strings are a 7-bit varint length in UTF-16 code units followed by UTF-16LE data.
        ///
        /// The EOF check at the end is not a nicety. The format has no internal framing or
        /// checksum, so a field width guessed wrongly produces a parse that still "succeeds" with
        /// garbage GUIDs. Landing exactly on the final byte is the only available evidence that
        /// every width was right, and a wrong catalogue is worse than none - see
        /// <see cref="PlayerInventory"/> for what the client does with an item it is not told about.
        /// </summary>
        private void ReadInventoryDat(byte[] buffer)
        {
            var reader = new BinaryFields(buffer);

            var assumeCount = reader.UInt32();
            for (var i = 0; i < assumeCount; i++)
                AssumeUnlockedItemSets.Add(reader.Guid());

            var setCount = reader.UInt32();
            for (var i = 0; i < setCount; i++)
            {
                var set = new ItemSetDefinition { SetId = reader.Guid(), Name = reader.String() };
                ItemSets.Add(set);
                _setsById[set.SetId] = set;
            }

            var itemCount = reader.UInt32();
            for (var i = 0; i < itemCount; i++)
            {
                var item = new ItemDefinition
                {
                    ItemId = reader.Guid(),
                    SetId = reader.Guid(),
                    Rarity = (int)reader.UInt32(),
                    Name = reader.String(),
                };
                Items.Add(item);
                _itemIds.Add(item.ItemId);

                // Items whose set is not in the set table are campaign and promotional rewards.
                // The client cannot unlock those through UnlockInventoryItemSet either, so they
                // stay unattached here and are simply never granted.
                if (_setsById.TryGetValue(item.SetId, out var owner))
                    owner.Items.Add(item.ItemId);
            }

            if (reader.Position != buffer.Length)
            {
                throw new InvalidDataException(
                    "Inventory.dat parse consumed " + reader.Position + " of " + buffer.Length
                    + " bytes. The file layout is not what this build expects; refusing to use a "
                    + "catalogue that may be wrong.");
            }
        }

        /// <summary>
        /// The season reward track, as <c>CurrentSeasonConfig</c> reads it: level N is completed on
        /// crossing <c>SeasonRewards[N-1].CumulativeXpToComplete</c>, and completing it grants
        /// <c>RewardInventoryItemSetId</c>.
        /// </summary>
        private void ReadProgressionConfig(string text)
        {
            var root = Json.TryParse(text);
            if (root == null) return;

            var seasons = root["Seasons"];
            if (seasons != null && seasons.Count > 0)
                SeasonId = seasons[0]["Id"].AsStringOr("1");

            var ugc = root["UgcExperience"];
            if (ugc != null)
            {
                Experience.PlayLevel = ugc["PlayLevel"].AsIntOr(Experience.PlayLevel);
                Experience.WinLevel = ugc["WinLevel"].AsIntOr(Experience.WinLevel);
                Experience.RateLevel = ugc["RateLevel"].AsIntOr(Experience.RateLevel);
            }

            var rewards = root["SeasonRewards"];
            if (rewards == null) return;

            foreach (var reward in rewards.Items)
            {
                var setId = reward["RewardInventoryItemSetId"].AsStringOr(string.Empty);
                RewardTrack.Add(new RewardTrackEntry
                {
                    Level = reward["LevelId"].AsIntOr(0),
                    CumulativeXp = reward["CumulativeXpToComplete"].AsLongOr(0),
                    SetId = setId.Length == 0 ? null : setId,
                });
            }

            RewardTrack.Sort((a, b) => a.Level.CompareTo(b.Level));
        }

        public string Summary()
        {
            var granting = 0;
            for (var i = 0; i < RewardTrack.Count; i++)
                if (RewardTrack[i].SetId != null) granting++;

            return ItemSets.Count + " sets, " + Items.Count + " items, "
                   + RewardTrack.Count + " reward levels (" + granting + " granting a set), "
                   + AssumeUnlockedItemSets.Count + " unlocked for everyone";
        }

        /// <summary>
        /// A byte-aligned reader for the one file that needs it. Kept private and minimal rather
        /// than reaching for BinaryReader, because the string encoding here is Unity's
        /// (length in UTF-16 code units, not bytes) and wrapping BinaryReader to say that is more
        /// code than just doing it.
        /// </summary>
        private sealed class BinaryFields
        {
            private readonly byte[] _buffer;

            internal int Position { get; private set; }

            internal BinaryFields(byte[] buffer)
            {
                _buffer = buffer;
            }

            private void Require(int count)
            {
                if (Position + count > _buffer.Length)
                    throw new InvalidDataException("Inventory.dat ended mid-field.");
            }

            internal uint UInt32()
            {
                Require(4);
                var value = (uint)(_buffer[Position]
                                   | (_buffer[Position + 1] << 8)
                                   | (_buffer[Position + 2] << 16)
                                   | (_buffer[Position + 3] << 24));
                Position += 4;
                return value;
            }

            /// <summary>
            /// Lower-case hex of the raw 16 bytes, matching the client's own on-disk ordering
            /// rather than .NET's Guid formatting. These are compared against GUIDs the client
            /// sends us, so byte order has to match its, not one we would prefer.
            /// </summary>
            internal string Guid()
            {
                Require(16);
                var chars = new char[32];
                for (var i = 0; i < 16; i++)
                {
                    var b = _buffer[Position + i];
                    chars[i * 2] = HexDigit(b >> 4);
                    chars[i * 2 + 1] = HexDigit(b & 0xF);
                }
                Position += 16;
                return new string(chars);
            }

            private static char HexDigit(int value) =>
                (char)(value < 10 ? '0' + value : 'a' + (value - 10));

            internal string String()
            {
                var length = 0;
                var shift = 0;
                byte b;
                do
                {
                    Require(1);
                    b = _buffer[Position++];
                    length |= (b & 0x7F) << shift;
                    shift += 7;

                    if (shift > 28)
                        throw new InvalidDataException("Inventory.dat string length is out of range.");
                }
                while ((b & 0x80) != 0);

                var byteCount = length * 2;
                Require(byteCount);
                var value = Encoding.Unicode.GetString(_buffer, Position, byteCount);
                Position += byteCount;
                return value;
            }
        }
    }

    /// <summary>
    /// Null-tolerant readers, so parsing a document with a missing key reads as a default instead
    /// of needing a check at every access.
    /// </summary>
    internal static class JsonDefaults
    {
        internal static string AsStringOr(this Json value, string fallback) =>
            value == null ? fallback : value.AsString(fallback);

        internal static int AsIntOr(this Json value, int fallback) =>
            value == null ? fallback : value.AsInt(fallback);

        internal static long AsLongOr(this Json value, long fallback) =>
            value == null ? fallback : value.AsLong(fallback);

        internal static double AsDoubleOr(this Json value, double fallback) =>
            value == null ? fallback : value.AsDouble(fallback);

        internal static bool AsBoolOr(this Json value, bool fallback) =>
            value == null ? fallback : value.AsBool(fallback);
    }
}
