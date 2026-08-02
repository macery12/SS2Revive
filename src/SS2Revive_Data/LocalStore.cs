using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SS2ReviveData
{
    /// <summary>
    /// Everything the backend knows about one player.
    ///
    /// Grouped per player rather than split into parallel buckets the way the HTTP backend's store
    /// was. That layout existed because a shared server holds thousands of players and reads them
    /// by category; here there is one real player and at most a handful of remembered peers, and
    /// per-player grouping makes the file legible to whoever opens it.
    /// </summary>
    public sealed class PlayerRecord
    {
        public string PlayerId;
        public string UserName = string.Empty;
        public long SeasonXp;
        public long GlobalXp;
        public int SeasonLevel = 1;
        public int GlobalLevel = 1;
        public long UpdatedAt;

        public DailyChallengeRecord Challenges;
        public CampaignMirror Campaigns = new CampaignMirror();
        public PlayerInventory Inventory = new PlayerInventory();

        public Json ToStorage()
        {
            var record = Json.Object()
                .Add("playerId", PlayerId)
                .Add("userName", UserName)
                .Add("seasonXp", SeasonXp)
                .Add("globalXp", GlobalXp)
                .Add("seasonLevel", SeasonLevel)
                .Add("globalLevel", GlobalLevel)
                .Add("updatedAt", UpdatedAt)
                .Add("campaigns", Campaigns.ToStorage())
                .Add("inventory", Inventory.ToStorage());

            if (Challenges != null) record.Add("challenges", Challenges.ToStorage());
            return record;
        }

        public static PlayerRecord FromStorage(Json value)
        {
            var record = new PlayerRecord
            {
                PlayerId = value["playerId"].AsStringOr(string.Empty),
                UserName = value["userName"].AsStringOr(string.Empty),
                SeasonXp = value["seasonXp"].AsLongOr(0),
                GlobalXp = value["globalXp"].AsLongOr(0),
                SeasonLevel = value["seasonLevel"].AsIntOr(1),
                GlobalLevel = value["globalLevel"].AsIntOr(1),
                UpdatedAt = value["updatedAt"].AsLongOr(0),
                Campaigns = CampaignMirror.FromStorage(value["campaigns"]),
                Inventory = PlayerInventory.FromStorage(value["inventory"]),
            };

            var challenges = value["challenges"];
            if (challenges != null) record.Challenges = DailyChallengeRecord.FromStorage(challenges);

            return record;
        }
    }

    /// <summary>
    /// The whole of the backend's persistence: one JSON file, rewritten atomically.
    ///
    /// Not a database, on purpose. The working set is a single player's progression plus three
    /// daily challenges; the file is a couple of kilobytes and rewriting it is cheaper than the
    /// bookkeeping any incremental format would need. Being plain, indented JSON also means a
    /// player can read it, back it up, and fix it by hand - which matters more for a mod restoring
    /// a dead service than write throughput ever will.
    ///
    /// Writes go to a temporary file and are then moved into place, so an interrupted write leaves
    /// the previous state intact rather than a truncated file. All access is serialised through
    /// <see cref="Gate"/>; the caller runs requests on a worker thread.
    /// </summary>
    public sealed class LocalStore
    {
        private const int CurrentVersion = 1;

        private readonly Dictionary<string, PlayerRecord> _players =
            new Dictionary<string, PlayerRecord>(StringComparer.Ordinal);

        private readonly string _file;
        private readonly Action<string> _warn;

        public readonly object Gate = new object();

        public string FilePath => _file;

        public LocalStore(string file, Action<string> warn = null)
        {
            _file = file;
            _warn = warn ?? delegate { };
            Load();
        }

        /// <summary>Never throws. A file we cannot read is reported and replaced by an empty state -
        /// the alternative is refusing to start the game over a corrupt progress file.</summary>
        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;

                var root = Json.TryParse(File.ReadAllText(_file, Encoding.UTF8));
                var players = root?["players"];
                if (players == null)
                {
                    _warn("Progress file at " + _file + " is not readable; starting from empty.");
                    return;
                }

                foreach (var entry in players.Items)
                {
                    var record = PlayerRecord.FromStorage(entry);
                    if (!PlayerIds.IsWellFormed(record.PlayerId)) continue;
                    _players[record.PlayerId] = record;
                }
            }
            catch (Exception ex)
            {
                _warn("Could not read " + _file + " (" + ex.Message + "); starting from empty.");
                _players.Clear();
            }
        }

        /// <summary>Call with <see cref="Gate"/> held.</summary>
        public PlayerRecord GetOrCreate(string playerId)
        {
            if (_players.TryGetValue(playerId, out var record)) return record;

            record = new PlayerRecord
            {
                PlayerId = playerId,
                UserName = PlayerIds.PlaceholderName(playerId),
            };
            _players[playerId] = record;
            return record;
        }

        /// <summary>Call with <see cref="Gate"/> held.</summary>
        public PlayerRecord Find(string playerId)
        {
            return _players.TryGetValue(playerId, out var record) ? record : null;
        }

        public int PlayerCount
        {
            get { lock (Gate) return _players.Count; }
        }

        /// <summary>
        /// Rewrites the file. Call with <see cref="Gate"/> held.
        ///
        /// Failure is logged and swallowed: losing a save is bad, but taking the game down at the
        /// end of a match because a disk was briefly busy is worse, and the next mutation writes
        /// the same state again.
        /// </summary>
        public void Save()
        {
            try
            {
                var players = Json.Array();
                foreach (var record in _players.Values)
                    players.Add(record.ToStorage());

                var root = Json.Object()
                    .Add("version", CurrentVersion)
                    .Add("savedAt", Clock.NowMs())
                    .Add("players", players);

                var directory = Path.GetDirectoryName(_file);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var temporary = _file + ".tmp";
                File.WriteAllText(temporary, Indent(root.ToString()), Encoding.UTF8);

                // File.Move refuses to overwrite on .NET Framework, so the old file goes first.
                // The window between the two is why the temporary file is kept rather than
                // written in place: a crash inside it loses at most the newest mutation.
                if (File.Exists(_file)) File.Delete(_file);
                File.Move(temporary, _file);
            }
            catch (Exception ex)
            {
                _warn("Could not write " + _file + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Re-indents compact JSON for the file on disk only.
        ///
        /// The writer emits compact output because that is what goes to the game, where every byte
        /// is copied into a buffer and parsed by hand. The save file has the opposite priority: a
        /// player opening it should be able to see what is in there.
        /// </summary>
        private static string Indent(string compact)
        {
            var output = new StringBuilder(compact.Length * 2);
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = 0; i < compact.Length; i++)
            {
                var c = compact[i];

                if (inString)
                {
                    output.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        output.Append(c);
                        break;

                    case '{':
                    case '[':
                        output.Append(c);
                        // An empty object or array stays on one line; a newline before the
                        // matching close would read as a missing member.
                        if (i + 1 < compact.Length && (compact[i + 1] == '}' || compact[i + 1] == ']'))
                        {
                            output.Append(compact[++i]);
                            break;
                        }
                        depth++;
                        NewLine(output, depth);
                        break;

                    case '}':
                    case ']':
                        depth--;
                        NewLine(output, depth);
                        output.Append(c);
                        break;

                    case ',':
                        output.Append(c);
                        NewLine(output, depth);
                        break;

                    case ':':
                        output.Append(": ");
                        break;

                    default:
                        output.Append(c);
                        break;
                }
            }

            output.Append('\n');
            return output.ToString();
        }

        private static void NewLine(StringBuilder output, int depth)
        {
            output.Append('\n');
            output.Append(' ', depth * 2);
        }
    }

    /// <summary>
    /// One place that decides what "now" is, so tests and the day-rollover logic can agree on it
    /// without every call site threading a timestamp through.
    /// </summary>
    public static class Clock
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Func<long> Wall =
            () => (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;

        private static Func<long> _source = Wall;

        /// <summary>Overridable so the daily-challenge rollover can be exercised without waiting
        /// for UTC midnight. Assigning null restores the wall clock rather than breaking it.</summary>
        public static Func<long> Source
        {
            get { return _source; }
            set { _source = value ?? Wall; }
        }

        public static long NowMs() => _source();
    }
}
