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
        public int SeasonLevel = 1;

        /// <summary>
        /// Kept separately from <see cref="SeasonLevel"/> only because the client reads it as its
        /// own field. There is no separate global XP counter: <c>LocalBackend.ProgressionJson</c>
        /// serves the season total for both, because the client displays the season figures and
        /// reads only the *level* out of the global pair - so a second independent total would be
        /// a number this backend invented rather than one it observed.
        /// </summary>
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
    /// Writes go through <see cref="AtomicFile"/>, so an interrupted write leaves the previous
    /// state intact rather than a truncated file - and this is the one file in the mod whose loss
    /// cannot be recovered from anywhere else, so it also keeps a <c>.bak</c> of the copy it
    /// displaced. All access is serialised through <see cref="Gate"/>; the caller runs requests on
    /// a worker thread.
    /// </summary>
    public sealed class LocalStore
    {
        private const int CurrentVersion = 1;

        private readonly Dictionary<string, PlayerRecord> _players =
            new Dictionary<string, PlayerRecord>(StringComparer.Ordinal);

        private readonly string _file;
        private readonly Action<string> _warn;

        /// <summary>
        /// Set when the file on disk was written by a newer build than this one. Everything still
        /// works for the session - the player gets a fresh, empty state and can play - but nothing
        /// is written back, because the alternative is overwriting a save this build could not
        /// read with one that has none of its contents.
        /// </summary>
        private bool _readOnly;

        public readonly object Gate = new object();

        public string FilePath => _file;

        /// <summary>True when the save on disk is from a newer format and is being left alone.</summary>
        public bool IsReadOnly => _readOnly;

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

                // The version has been written since the first release; reading it is what makes
                // it worth anything. A file from a future build may have moved a field this one
                // still reads, and interpreting it anyway would not fail loudly - it would load a
                // plausible-looking save with the wrong numbers in it, and then overwrite the real
                // one on the next mutation. Refusing costs a session's progress; guessing costs
                // all of it.
                var version = root["version"].AsIntOr(1);
                if (version > CurrentVersion)
                {
                    _warn("Progress file at " + _file + " was written by a newer version of "
                          + "SS2Revive (format " + version + ", this build reads " + CurrentVersion
                          + "). Refusing to read it rather than risk misreading it - the file has "
                          + "been left alone. Update the mod, or move that file aside to start "
                          + "fresh.");
                    _readOnly = true;
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

        /// <summary>
        /// Rewrites the file. Call with <see cref="Gate"/> held.
        ///
        /// Failure is logged and swallowed: losing a save is bad, but taking the game down at the
        /// end of a match because a disk was briefly busy is worse, and the next mutation writes
        /// the same state again.
        /// </summary>
        public void Save()
        {
            if (_readOnly) return;

            try
            {
                var players = Json.Array();
                foreach (var record in _players.Values)
                    players.Add(record.ToStorage());

                var root = Json.Object()
                    .Add("version", CurrentVersion)
                    .Add("savedAt", Clock.NowMs())
                    .Add("players", players);

                AtomicFile.WriteAllText(_file, Indent(root.ToString()), Encoding.UTF8,
                                        keepBackup: true);
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
