using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SS2ReviveData;

namespace SS2ReviveData.Tests
{
    /// <summary>
    /// Exercises the serverless backend end to end, against the real installed game files.
    ///
    /// Run with:  dotnet run --project tests\DataTests
    /// Optionally pass the game folder:  dotnet run --project tests\DataTests -- "C:\path\to\game"
    /// </summary>
    internal static class Program
    {
        private const string PlayerA = "STEAM-76561198000000001-------------";
        private const string PlayerB = "STEAM-76561198000000002-------------";

        private static int _failures;
        private static string _section = string.Empty;

        private static int Main(string[] args)
        {
            var gameRoot = args.Length > 0 ? args[0] : FindGameDirectory();

            var scratch = Path.Combine(Path.GetTempPath(),
                "ss2revive-tests-" + Guid.NewGuid().ToString("N"));

            try
            {
                RunJsonTests();
                RunPlayerIdTests();
                RunCatalogueTests(gameRoot, scratch);
                RunBackendTests(gameRoot, scratch);
                RunChallengeRolloverTests(scratch);
                RunUgcStoreTests(scratch);
            }
            catch (Exception ex)
            {
                Console.WriteLine("UNCAUGHT in '" + _section + "': " + ex);
                _failures++;
            }
            finally
            {
                Clock.Source = null;
                TryDelete(scratch);
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "All checks passed."
                : _failures + " check(s) failed.");

            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------- game files

        /// <summary>
        /// Finds the installed game, so no path from one machine ends up committed.
        ///
        /// First choice is whatever MSBuild resolved at build time, which honours
        /// Directory.Build.user.props and so covers an install Steam does not know about. After
        /// that, Steam's own libraries.
        ///
        /// The Steam scan is deliberately package-free: reading the install path out of the
        /// registry would mean a NuGet dependency on Microsoft.Win32.Registry, and the point of
        /// this runner is that "dotnet run" needs nothing restored. libraryfolders.vdf gets to the
        /// same answer with File.ReadAllText.
        ///
        /// Returning something wrong is harmless. The catalogue tests look for Inventory.dat and
        /// report SKIP when it is not there.
        /// </summary>
        private static string FindGameDirectory()
        {
            const string relative = @"steamapps\common\Surgeon Simulator 2";

            var configured = Environment.GetEnvironmentVariable("SS2_GAME_DIR");
            if (!string.IsNullOrEmpty(configured))
                return configured;

            foreach (var metadata in typeof(Program).Assembly
                         .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
            {
                var pair = (AssemblyMetadataAttribute)metadata;
                if (pair.Key == "GameDir" && !string.IsNullOrEmpty(pair.Value))
                    return pair.Value;
            }

            var roots = new List<string>();
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.ProgramFiles,
                     })
            {
                var basePath = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(basePath))
                    roots.Add(Path.Combine(basePath, "Steam"));
            }

            // Library folders on other drives are listed in the default install's own vdf. The
            // format is loose, so pull anything that looks like a path and test it rather than
            // trying to parse the structure.
            foreach (var root in new List<string>(roots))
            {
                var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf))
                    continue;

                foreach (var line in File.ReadAllLines(vdf))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var open = trimmed.IndexOf('"', 6);
                    var close = open < 0 ? -1 : trimmed.IndexOf('"', open + 1);
                    if (close > open)
                        roots.Add(trimmed.Substring(open + 1, close - open - 1).Replace(@"\\", @"\"));
                }
            }

            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, relative);
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return roots.Count > 0 ? Path.Combine(roots[0], relative) : relative;
        }

        // ------------------------------------------------------------------ json

        /// <summary>
        /// The writer's escaping rules are the load-bearing part. Bossa's ManualJsonDeserializer
        /// drops a backslash and keeps the next character, full stop - so only \" and \\ survive a
        /// round trip, and anything else this writer emitted would arrive as literal text.
        /// </summary>
        private static void RunJsonTests()
        {
            Section("json");

            Check("integers keep their shape",
                Json.Object().Add("n", 150).ToString() == "{\"n\":150}");

            Check("large timestamps do not go exponential",
                Json.Object().Add("t", 1754150400000L).ToString() == "{\"t\":1754150400000}");

            Check("quotes are escaped",
                Json.Str("he said \"hi\"").ToString() == "\"he said \\\"hi\\\"\"");

            Check("backslashes are escaped",
                Json.Str("a\\b").ToString() == "\"a\\\\b\"");

            // The two escapes the client cannot decode must never be emitted.
            var control = Json.Str("line\nbreak\ttab").ToString();
            Check("newlines and tabs are dropped, not escaped",
                control == "\"linebreaktab\"" && !control.Contains("\\n") && !control.Contains("\\t"));

            // Non-ASCII is fine: the response is handed over as a UTF-16 buffer and read a char at
            // a time, so a persona name in any script passes through untouched.
            Check("non-ascii passes through raw",
                Json.Str("Ünïcodé 日本").ToString() == "\"Ünïcodé 日本\"");

            // Nested JSON inside a string value - how `metadata` and `information` travel.
            var inner = Json.Object().Add("bestTime", 12.5).Add("bestBloodLoss", 3);
            var outer = Json.Object().Add("information", inner.ToString()).ToString();
            var reparsed = Json.Parse(outer)["information"].AsString();
            Check("nested json survives being a string value",
                Json.Parse(reparsed)["bestTime"].AsDouble() == 12.5);

            Check("parser handles escapes the client never emits",
                Json.Parse("{\"a\":\"x\\u0041y\"}")["a"].AsString() == "xAy");

            Check("missing keys read as null",
                Json.Parse("{}")["nope"] == null);

            Check("malformed input is refused, not guessed at",
                Json.TryParse("{\"a\":") == null);

            Check("round trip of a nested document",
                Json.Parse("{\"a\":[1,2,{\"b\":true}]}")["a"][2]["b"].AsBool());
        }

        // ------------------------------------------------------------ player ids

        private static void RunPlayerIdTests()
        {
            Section("player ids");

            var id = PlayerIds.FromSteamId(76561198000000001UL);
            Check("ids are exactly 36 characters", id.Length == PlayerIds.Width);
            Check("ids round trip back to the steam account",
                PlayerIds.ToSteamId(id) == 76561198000000001UL);
            Check("ids we did not mint decode to nothing",
                PlayerIds.ToSteamId("00000000-0000-0000-0000-000000000000") == 0UL);
            Check("a short id is rejected", !PlayerIds.IsWellFormed("STEAM-1"));
        }

        // -------------------------------------------------------------- catalogue

        private static void RunCatalogueTests(string gameRoot, string scratch)
        {
            Section("catalogue");

            RunCatalogueFixtureTests(scratch);

            var contentDirectory = GameCatalogue.FindContentDirectory(gameRoot);
            if (contentDirectory == null)
            {
                Console.WriteLine("  SKIP  no Inventory.dat under " + gameRoot);
                return;
            }

            var warnings = new List<string>();
            var catalogue = GameCatalogue.Load(contentDirectory, warnings.Add);
            Console.WriteLine("  read  " + catalogue.Summary());
            foreach (var warning in warnings)
                Console.WriteLine("  warn  " + warning);

            // Load() throws unless every set a set-owning item names is actually defined, so
            // reaching here is already the strongest available evidence that every field width was
            // right - see GameCatalogue.Validate.
            Check("the set table is populated", catalogue.ItemSets.Count > 0);
            Check("the item table is populated", catalogue.Items.Count > 0);
            Check("some sets are free for everyone", catalogue.AssumeUnlockedItemSets.Count > 0);
            Check("guids are 32 lower-case hex characters",
                catalogue.Items[0].ItemId.Length == 32
                && catalogue.Items[0].ItemId == catalogue.Items[0].ItemId.ToLowerInvariant());
            Check("item names decoded as text, not as bytes",
                !string.IsNullOrWhiteSpace(catalogue.Items[0].Name));
            Check("the reward track was read", catalogue.RewardTrack.Count > 0);
            Check("the reward track is ordered by level",
                catalogue.RewardTrack[0].Level < catalogue.RewardTrack[catalogue.RewardTrack.Count - 1].Level);
            Check("ugc experience rules are sane",
                catalogue.Experience.PlayLevel > 0 && catalogue.Experience.WinLevel > 0);

            // The off-by-one that the reward-track code exists to get right: completing level N is
            // the moment XP reaches entry N's threshold, at which point the level already reads
            // N+1. Granting by level rather than by XP hands out one set too many.
            var first = catalogue.RewardTrack[0];
            var belowThreshold = PlayerInventory.RewardSetsForXp(catalogue, first.CumulativeXp - 1);
            var atThreshold = PlayerInventory.RewardSetsForXp(catalogue, first.CumulativeXp);
            Check("no reward before the first threshold", belowThreshold.Count == 0);
            Check("exactly one reward on crossing the first threshold",
                atThreshold.Count == (first.SetId == null ? 0 : 1));

            Check("level 1 at zero xp", PlayerInventory.SeasonLevelForXp(catalogue, 0) == 1);
            Check("level 2 on crossing the first threshold",
                PlayerInventory.SeasonLevelForXp(catalogue, first.CumulativeXp) == 2);
            Check("the level stops climbing past the end of the track",
                PlayerInventory.SeasonLevelForXp(catalogue, long.MaxValue / 2) == catalogue.RewardTrack.Count);

            // Not vacuous: the cross-reference above only proves anything while most items name a
            // set. If a future build stopped attaching items to sets, Validate would still pass and
            // would no longer be checking anything, and this is where that shows up.
            var referencing = 0;
            foreach (var item in catalogue.Items)
                if (item.SetId != NullGuid) referencing++;

            Check("most items name a set, which is what makes the parse checkable",
                referencing > catalogue.Items.Count / 2);
        }

        private const string NullGuid = "00000000000000000000000000000000";

        /// <summary>
        /// The two things the shipped file cannot demonstrate on its own: that a build carrying
        /// trailing bytes is still read (1.3.7.3054 repeats part of its own tail), and that a
        /// misread is still refused rather than handed over as a catalogue.
        /// </summary>
        private static void RunCatalogueFixtureTests(string scratch)
        {
            var setId = new byte[16];
            var itemId = new byte[16];
            for (var i = 0; i < 16; i++) { setId[i] = (byte)(i + 1); itemId[i] = (byte)(0xF0 - i); }

            var good = BuildInventoryDat(setId, itemId, itemSetId: setId);

            var warnings = new List<string>();
            var catalogue = LoadFixture(scratch, "clean", good, warnings.Add);
            Check("a well formed fixture reads", catalogue != null
                && catalogue.ItemSets.Count == 1 && catalogue.Items.Count == 1);
            Check("and reports nothing", warnings.Count == 0);

            // What 1.3.7.3054 actually ships: bytes past the last declared record.
            var trailing = new List<byte>(good);
            trailing.AddRange(good);
            warnings.Clear();
            catalogue = LoadFixture(scratch, "trailing", trailing.ToArray(), warnings.Add);
            Check("trailing bytes do not lose the catalogue", catalogue != null
                && catalogue.ItemSets.Count == 1 && catalogue.Items.Count == 1);
            Check("but are reported", warnings.Count == 1
                && warnings[0].Contains(good.Length.ToString()));

            // A width read wrongly lands mid-record, and the set the item names stops resolving.
            var orphan = BuildInventoryDat(setId, itemId, itemSetId: itemId);
            warnings.Clear();
            Check("an item pointing at an undefined set is refused",
                LoadFixture(scratch, "orphan", orphan, warnings.Add) == null);

            var shifted = new List<byte>(good);
            shifted.Insert(0, 0);
            Check("a file shifted out of alignment is refused",
                LoadFixture(scratch, "shifted", shifted.ToArray(), _ => { }) == null);
        }

        /// <summary>One assume-unlocked set, one set, one item, in the layout the client writes.</summary>
        private static byte[] BuildInventoryDat(byte[] setId, byte[] itemId, byte[] itemSetId)
        {
            var bytes = new List<byte>();

            void Count(int value) => bytes.AddRange(BitConverter.GetBytes(value));
            void Text(string value)
            {
                bytes.Add((byte)value.Length);            // varint, short enough to be one byte
                foreach (var c in value)
                {
                    bytes.Add((byte)(c & 0xFF));
                    bytes.Add((byte)(c >> 8));
                }
            }

            Count(1);
            bytes.AddRange(setId);                        // assumeUnlockedItemSets

            Count(1);
            bytes.AddRange(setId);
            Text("Test Set");                             // itemSetDefinitions

            Count(1);
            bytes.AddRange(itemId);
            bytes.AddRange(itemSetId);
            Count(0);                                     // rarity
            Text("test_hat_01");                          // itemDefinitions

            return bytes.ToArray();
        }

        private static GameCatalogue LoadFixture(
            string scratch, string name, byte[] bytes, Action<string> warn)
        {
            var directory = Path.Combine(scratch, "catalogue", name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "Inventory.dat"), bytes);

            try
            {
                return GameCatalogue.Load(directory, warn);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- backend

        private static void RunBackendTests(string gameRoot, string scratch)
        {
            Section("backend");

            var backend = new LocalBackend(new LocalBackendOptions
            {
                ContentDirectory = gameRoot,
                SaveDirectory = Path.Combine(scratch, "grantAll"),
                GrantAllCosmetics = true,
                LocalPlayerId = PlayerA,
            }, _ => { }, message => Console.WriteLine("  warn  " + message));

            // -- challenges

            var daily = Ok(backend, "GET", "/challenges/challenges/players/" + PlayerA + "/currentDailyChallenges");
            var challenges = daily["challenges"];
            Check("exactly three challenges, which the client requires", challenges.Count == 3);
            Check("the player id round trips", daily["playerId"].AsString() == PlayerA);
            Check("challenges expire a day after they were created",
                daily["expiresAt"].AsLong() - daily["createdAt"].AsLong() == 86_400_000L);

            var ids = new List<string>();
            foreach (var challenge in challenges.Items)
            {
                ids.Add(challenge["challengeId"].AsString());
                Check("metadata parses as its own document",
                    Json.TryParse(challenge["metadata"].AsString()) != null);
                Check("metadata names a statistic to count",
                    Json.Parse(challenge["metadata"].AsString())["LevelStatisticType"] != null);
            }
            Check("the three challenges are distinct",
                ids[0] != ids[1] && ids[1] != ids[2] && ids[0] != ids[2]);

            var again = Ok(backend, "GET", "/challenges/challenges/players/" + PlayerA + "/currentDailyChallenges");
            Check("re-reading the same day is stable",
                again["challenges"][0]["challengeId"].AsString() == ids[0]);

            var other = Ok(backend, "GET", "/challenges/challenges/players/" + PlayerB + "/currentDailyChallenges");
            Check("a different player gets their own set, seeded from their id",
                other["challenges"][0]["challengeId"].AsString() != ids[0]
                || other["challenges"][1]["challengeId"].AsString() != ids[1]);

            // Steps are an absolute total, not a delta, and must never move backwards.
            var target = challenges[0];
            var total = target["totalNumberOfSteps"].AsInt();
            var partial = Ok(backend, "POST",
                "/challenges/challenges/players/" + PlayerA + "/challenges/" + ids[0],
                "{\"stepsCompleted\":1}");
            Check("progress is recorded", FindChallenge(partial, ids[0])["stepsCompleted"].AsInt() == 1);

            var stale = Ok(backend, "POST",
                "/challenges/challenges/players/" + PlayerA + "/challenges/" + ids[0],
                "{\"stepsCompleted\":0}");
            Check("a late retry carrying an older value cannot undo progress",
                FindChallenge(stale, ids[0])["stepsCompleted"].AsInt() == 1);

            var over = Ok(backend, "POST",
                "/challenges/challenges/players/" + PlayerA + "/challenges/" + ids[0],
                "{\"stepsCompleted\":" + (total + 500) + "}");
            Check("steps are clamped to the total",
                FindChallenge(over, ids[0])["stepsCompleted"].AsInt() == total);

            var xpAfterFirst = Ok(backend, "GET",
                "/player-progression/playerProgression/players/" + PlayerA + "/progression")["currentSeasonXp"].AsLong();
            Check("completing a challenge paid its xp",
                xpAfterFirst == target["xpGain"].AsInt());

            Ok(backend, "POST", "/challenges/challenges/players/" + PlayerA + "/challenges/" + ids[0],
                "{\"stepsCompleted\":" + total + "}");
            var xpAfterRepeat = Ok(backend, "GET",
                "/player-progression/playerProgression/players/" + PlayerA + "/progression")["currentSeasonXp"].AsLong();
            Check("re-uploading the final step does not pay twice", xpAfterRepeat == xpAfterFirst);

            Check("an unknown challenge id is refused",
                backend.Handle("POST",
                    "/challenges/challenges/players/" + PlayerA + "/challenges/nope",
                    "{\"stepsCompleted\":1}").Status == 404);

            // -- progression and the mirror

            backend.MirrorProgression(PlayerA, 4321, 7, 7);
            var mirrored = Ok(backend, "GET",
                "/player-progression/playerProgression/players/" + PlayerA + "/progression");
            Check("the client's own figure wins over anything recorded here",
                mirrored["currentSeasonXp"].AsLong() == 4321 && mirrored["currentSeasonLevel"].AsInt() == 7);

            Check("a friend with no published level is refused rather than invented",
                backend.Handle("GET",
                    "/player-progression/playerProgression/players/" + PlayerB + "/progression",
                    null).Status == 404);

            // The identity arrives after construction: BepInEx runs a plugin's Awake long before
            // the game signs anybody in, so a backend embedded in the game is built before it can
            // know who "self" is.
            var late = new LocalBackend(new LocalBackendOptions
            {
                SaveDirectory = Path.Combine(scratch, "late-identity"),
            }, _ => { }, _ => { });
            Check("an unset identity means nobody is self, not everybody",
                late.Handle("GET", "/player-progression/playerProgression/players/" + PlayerA
                            + "/progression", null).Status == 404);
            late.LocalPlayerId = PlayerA;
            Check("setting it afterwards makes self progression resolve",
                late.Handle("GET", "/player-progression/playerProgression/players/" + PlayerA
                            + "/progression", null).Status == 200);

            backend.Peers = new StubPeers(PlayerB, 40, 99000, "Friend");
            var peer = Ok(backend, "GET",
                "/player-progression/playerProgression/players/" + PlayerB + "/progression");
            Check("a published level is served",
                peer["currentSeasonLevel"].AsInt() == 40 && peer["userName"].AsString() == "Friend");

            // -- campaigns

            var emptyCampaigns = Ok(backend, "GET", "/player-progression/players/" + PlayerA + "/campaigns");
            Check("the campaign mirror starts empty but structurally valid",
                emptyCampaigns["scores"].Count == 0 && emptyCampaigns["unlockedLevels"] != null);
            Check("the campaign player id round trips - a mismatch discards the whole response",
                emptyCampaigns["playerId"].AsString() == PlayerA);

            Ok(backend, "POST", "/player-progression/campaignLevels/lvl-1/players/" + PlayerA + "/completed",
                "{\"campaignId\":\"c1\",\"grade\":\"PASS\",\"information\":\"{\\\"bestTime\\\":90,\\\"bestBloodLoss\\\":40}\"}");
            Ok(backend, "POST", "/player-progression/campaignLevels/lvl-1/players/" + PlayerA + "/completed",
                "{\"campaignId\":\"c1\",\"grade\":\"A_PLUS_PLUS\",\"information\":\"{\\\"bestTime\\\":75,\\\"bestBloodLoss\\\":90}\"}");

            var campaigns = Ok(backend, "GET", "/player-progression/players/" + PlayerA + "/campaigns");
            Check("one entry per level, merged rather than appended", campaigns["scores"].Count == 1);
            var score = campaigns["scores"][0];
            Check("the grade is sent in its wire form, not the enum name",
                score["grade"].AsString() == "A++");
            var information = Json.Parse(score["information"].AsString());
            Check("the better time wins", information["bestTime"].AsDouble() == 75);
            Check("the better blood loss wins, independently of the time",
                information["bestBloodLoss"].AsDouble() == 40);

            Ok(backend, "POST", "/player-progression/campaignLevels/lvl-1/players/" + PlayerA + "/completed",
                "{\"campaignId\":\"c1\",\"grade\":\"FAIL\",\"information\":\"{\\\"bestTime\\\":999}\"}");
            var afterFail = Ok(backend, "GET", "/player-progression/players/" + PlayerA + "/campaigns");
            Check("a later worse run cannot roll the mirror backwards",
                afterFail["scores"][0]["grade"].AsString() == "A++");

            // -- cosmetics

            if (backend.CosmeticsAvailable)
            {
                var sets = Ok(backend, "GET", "/player-progression/itemSets");
                Check("item sets are objects keyed itemId, not bare strings",
                    sets.Count > 0 && sets[0]["items"] != null);

                var inventory = Ok(backend, "GET",
                    "/player-progression/playerProgression/players/" + PlayerA + "/inventory");
                Check("the inventory key is 'inventory', which is what the client reads",
                    inventory["inventory"] != null);
                Check("granting everything means every grantable item is present",
                    inventory["inventory"].Count == GrantableItemCount(backend.Catalogue));

                var firstItem = inventory["inventory"][0]["itemId"].AsString();
                var character = Ok(backend, "POST",
                    "/player-progression/playerProgression/players/" + PlayerA + "/addCharacter",
                    "{\"characterId\":\"CHAR-1\",\"itemsToEquip\":[\"" + firstItem + "\",\"not-a-real-item\"]}");
                Check("unknown item ids are filtered out, as the client does locally",
                    character["equippedItemIds"].Count == 1
                    && character["equippedItemIds"][0].AsString() == firstItem);

                var equipped = Ok(backend, "POST",
                    "/player-progression/playerProgression/players/" + PlayerA
                    + "/characters/char-1/setEquippedItems", "{\"equippedItems\":[]}");
                Check("equipping on a known character updates it rather than adding a second",
                    equipped["equippedItemIds"].Count == 0);

                var reread = Ok(backend, "GET",
                    "/player-progression/playerProgression/players/" + PlayerA + "/inventory");
                Check("one character, not two", reread["characters"].Count == 1);

                var completed = Ok(backend, "POST", "/player-progression/quickplay/completeLevel",
                    "{\"playerId\":\"" + PlayerA + "\",\"levelWon\":true}");
                Check("the xp post answers with the season figures",
                    completed["seasonExperience"].AsLong()
                    == 4321 + backend.Catalogue.Experience.PlayLevel + backend.Catalogue.Experience.WinLevel);
            }
            else
            {
                Console.WriteLine("  SKIP  cosmetics (no catalogue)");
            }

            // -- misc

            Check("favourite levels answer once rather than being refused repeatedly",
                backend.Handle("GET", "/player-progression/players/" + PlayerA + "/favouriteLevels", null).Body == "[]");
            Check("a profile is served without any profile server",
                Ok(backend, "GET", "/profile/players/" + PlayerA)["playerId"].AsString() == PlayerA);
            Check("an unknown endpoint fails with 404, never 5xx",
                backend.Handle("GET", "/nope/at/all", null).Status == 404);
            Check("a malformed player id is a 400, not a crash",
                backend.Handle("GET", "/profile/players/short", null).Status == 400);
            Check("a query string does not defeat route matching",
                backend.Handle("GET", "/player-progression/players/" + PlayerA + "/campaigns?x=1", null).Status == 200);

            // -- persistence

            var reloaded = new LocalBackend(new LocalBackendOptions
            {
                ContentDirectory = gameRoot,
                SaveDirectory = Path.Combine(scratch, "grantAll"),
                GrantAllCosmetics = true,
                LocalPlayerId = PlayerA,
            }, _ => { }, message => Console.WriteLine("  warn  " + message));

            var survived = Ok(reloaded, "GET",
                "/player-progression/playerProgression/players/" + PlayerA + "/progression");
            Check("progression survives a restart", survived["currentSeasonXp"].AsLong() >= 4321);

            var survivedCampaigns = Ok(reloaded, "GET", "/player-progression/players/" + PlayerA + "/campaigns");
            Check("campaign grades survive a restart",
                survivedCampaigns["scores"][0]["grade"].AsString() == "A++");

            var survivedChallenges = Ok(reloaded, "GET",
                "/challenges/challenges/players/" + PlayerA + "/currentDailyChallenges");
            Check("a restart does not reroll a half-finished day",
                survivedChallenges["challenges"][0]["challengeId"].AsString() == ids[0]);

            // -- progressive unlocks, the non-default posture

            if (backend.CosmeticsAvailable)
            {
                var earned = new LocalBackend(new LocalBackendOptions
                {
                    ContentDirectory = gameRoot,
                    SaveDirectory = Path.Combine(scratch, "earned"),
                    GrantAllCosmetics = false,
                    LocalPlayerId = PlayerA,
                }, _ => { }, message => Console.WriteLine("  warn  " + message));

                var free = Ok(earned, "GET",
                    "/player-progression/playerProgression/players/" + PlayerA + "/inventory");
                var freeCount = free["inventory"].Count;
                Check("the sets everyone starts with are never withheld", freeCount > 0);
                Check("but not everything is handed over",
                    freeCount < GrantableItemCount(earned.Catalogue));

                var firstReward = FirstRewardEntry(earned.Catalogue);
                earned.MirrorProgression(PlayerA, firstReward.CumulativeXp, 2, 2);
                var afterLevel = Ok(earned, "GET",
                    "/player-progression/playerProgression/players/" + PlayerA + "/inventory");
                Check("crossing a reward threshold unlocks that level's set",
                    afterLevel["inventory"].Count > freeCount);
            }
        }

        // ------------------------------------------------------------- rollover

        /// <summary>
        /// The daily rotation, without waiting until UTC midnight. Also the one place the
        /// deterministic-per-day property is checked directly: it is what made challenges a
        /// per-player thing all along, and therefore why losing the server costs nothing here.
        /// </summary>
        private static void RunChallengeRolloverTests(string scratch)
        {
            Section("daily rollover");

            // Floored rather than written out, so it is a UTC midnight by construction: the window
            // logic is exactly what is under test here, and a hand-picked constant that turned out
            // to be mid-afternoon would fail the check for the wrong reason.
            const long day = 86_400_000L;
            var now = 1_754_150_400_000L / day * day;
            Clock.Source = () => now;

            var backend = new LocalBackend(new LocalBackendOptions
            {
                SaveDirectory = Path.Combine(scratch, "rollover"),
                LocalPlayerId = PlayerA,
            }, _ => { }, _ => { });

            var day1 = Ok(backend, "GET", "/challenges/challenges/players/" + PlayerA + "/currentDailyChallenges");
            var day1First = day1["challenges"][0]["challengeId"].AsString();
            Check("the window starts at the UTC midnight it was asked on",
                day1["createdAt"].AsLong() == now);

            Ok(backend, "POST", "/challenges/challenges/players/" + PlayerA + "/challenges/" + day1First,
                "{\"stepsCompleted\":1}");

            now += day;
            var day2 = Ok(backend, "GET", "/challenges/challenges/players/" + PlayerA + "/currentDailyChallenges");
            Check("the next UTC day rolls a fresh window",
                day2["createdAt"].AsLong() == now);
            Check("and resets progress",
                day2["challenges"][0]["stepsCompleted"].AsInt() == 0);

            // Same player, same day, from nothing: the roll has to be reproducible or a reinstall
            // would hand back a different set mid-day.
            var elsewhere = new LocalBackend(new LocalBackendOptions
            {
                SaveDirectory = Path.Combine(scratch, "rollover-2"),
                LocalPlayerId = PlayerA,
            }, _ => { }, _ => { });
            var reproduced = Ok(elsewhere, "GET",
                "/challenges/challenges/players/" + PlayerA + "/currentDailyChallenges");
            Check("the same player on the same day gets the same three challenges anywhere",
                reproduced["challenges"][0]["challengeId"].AsString()
                == day2["challenges"][0]["challengeId"].AsString());

            var forced = Ok(backend, "POST", "/challenges/challenges/forceResetPlayerDailyChallenges",
                "{\"playerId\":\"" + PlayerA + "\"}");
            Check("a forced reset still returns three", forced["challenges"].Count == 3);

            Clock.Source = null;
        }

        // ---------------------------------------------------------------- helpers

        private sealed class StubPeers : IPeerDirectory
        {
            private readonly string _playerId;
            private readonly int _level;
            private readonly long _xp;
            private readonly string _name;

            internal StubPeers(string playerId, int level, long xp, string name)
            {
                _playerId = playerId;
                _level = level;
                _xp = xp;
                _name = name;
            }

            public bool TryGetLevel(string playerId, out int seasonLevel, out long seasonXp,
                                    out string userName)
            {
                seasonLevel = _level;
                seasonXp = _xp;
                userName = _name;
                return playerId == _playerId;
            }
        }

        private static Json FindChallenge(Json response, string challengeId)
        {
            foreach (var challenge in response["challenges"].Items)
            {
                if (challenge["challengeId"].AsString() == challengeId) return challenge;
            }
            throw new InvalidOperationException("Challenge " + challengeId + " missing from the response.");
        }

        private static int GrantableItemCount(GameCatalogue catalogue)
        {
            var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in catalogue.ItemSets)
            {
                foreach (var itemId in set.Items) items.Add(itemId);
            }
            return items.Count;
        }

        private static RewardTrackEntry FirstRewardEntry(GameCatalogue catalogue)
        {
            foreach (var entry in catalogue.RewardTrack)
            {
                if (entry.SetId != null) return entry;
            }
            throw new InvalidOperationException("The reward track grants no sets at all.");
        }

        private static Json Ok(LocalBackend backend, string verb, string path, string body = null)
        {
            var response = backend.Handle(verb, path, body);
            if (response.Status != 200)
            {
                throw new InvalidOperationException(
                    verb + " " + path + " answered " + response.Status + ": " + response.Body);
            }

            var parsed = Json.TryParse(response.Body);
            if (parsed == null)
            {
                throw new InvalidOperationException(
                    verb + " " + path + " answered unparseable JSON: " + response.Body);
            }

            return parsed;
        }

        private static void RunUgcStoreTests(string scratch)
        {
            Section("level library");

            var root = Path.Combine(scratch, "ugc");
            var store = new UgcStore(root);

            var content = new byte[] { 1, 2, 3, 4 };
            var image = new byte[] { 9, 9, 9 };
            var level = store.Create("My Level", "a description",
                new List<string> { PlayerA }, new List<string> { "TEAM_COOP" },
                29, content, image);

            Check("creating a level returns an id", !string.IsNullOrEmpty(level.Id));
            Check("the first save is revision 1", level.LatestContent().ContentVersion == 1);
            Check("a thumbnail is set on creation", !string.IsNullOrEmpty(level.ThumbnailKey));

            var contentKey = level.LatestContent().Key;
            Check("content keys are recognisable as ours", UgcStore.OwnsKey(contentKey));
            Check("content reads back byte for byte",
                BytesEqual(store.ReadKey(contentKey), content));
            Check("the thumbnail reads back", BytesEqual(store.ReadKey(level.ThumbnailKey), image));

            // The containment check is the reason keys are resolved rather than concatenated.
            string escaped;
            Check("a key cannot climb out of the library",
                !store.TryResolveKey(UgcStore.KeyPrefix + "../../secrets.txt", out escaped));
            Check("a key we did not mint is refused",
                !store.TryResolveKey("some/s3/object", out escaped));

            store.AddContent(level, 29, new byte[] { 5, 6 }, null, false);
            Check("saving again adds a revision", level.Contents.Count == 2);
            Check("and bumps the revision number", level.LatestContent().ContentVersion == 2);
            Check("the newest revision is the one just written",
                BytesEqual(store.ReadKey(level.LatestContent().Key), new byte[] { 5, 6 }));

            // Autosaves are what make pruning necessary, so prune with autosaves.
            var manualKey = level.LatestContent().Key;
            for (var i = 0; i < 40; i++) store.AddContent(level, 29, new byte[] { (byte)i }, null, true);

            Check("revisions are capped", level.Contents.Count <= 24);
            Check("the last manual save survives pruning",
                BytesEqual(store.ReadKey(manualKey), new byte[] { 5, 6 }));

            // A second store over the same folder is what happens on the next launch.
            var reopened = new UgcStore(root);
            var reloaded = reopened.Get(level.Id);
            Check("levels survive a restart", reloaded != null);
            Check("titles survive a restart", reloaded.Title == "My Level");
            Check("tags survive a restart", reloaded.Tags.Contains("TEAM_COOP"));
            Check("creators survive a restart", reloaded.CreatorIds.Contains(PlayerA));
            Check("revision history survives a restart",
                reloaded.Contents.Count == level.Contents.Count);

            var other = reopened.Create("Someone Else's", "", new List<string> { PlayerB },
                                        null, 29, new byte[] { 7 }, null);
            other.Status = UgcStore.StatusPublished;
            reopened.Update(other);

            int pages;
            var mine = reopened.Search(new UgcQuery { Status = UgcStore.StatusDraft, CreatorId = PlayerA },
                                       out pages);
            Check("a draft search returns only my drafts", mine.Count == 1 && mine[0].Id == level.Id);

            var published = reopened.Search(new UgcQuery { Status = UgcStore.StatusPublished }, out pages);
            Check("a published search returns only published levels",
                published.Count == 1 && published[0].Id == other.Id);

            var byTitle = reopened.Search(
                new UgcQuery { AnyStatus = true, TitleContains = "someone else" }, out pages);
            Check("title search is case insensitive and partial", byTitle.Count == 1);

            var paged = reopened.Search(new UgcQuery { AnyStatus = true, ResultsPerPage = 1 }, out pages);
            Check("paging splits the results", paged.Count == 1 && pages == 2);

            var empty = reopened.Search(new UgcQuery { Tag = "NOTHING_HAS_THIS", AnyStatus = true },
                                        out pages);
            Check("an empty result still reports one page", empty.Count == 0 && pages == 1);

            Check("deleting a level reports success", reopened.Delete(level.Id));
            Check("and it is gone from the index", reopened.Get(level.Id) == null);
            Check("and its blobs are gone too", reopened.ReadKey(contentKey) == null);
            Check("deleting it twice is not an error", !reopened.Delete(level.Id));
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null) return left == right;
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static void Section(string name)
        {
            _section = name;
            Console.WriteLine();
            Console.WriteLine("== " + name);
        }

        private static void Check(string description, bool condition)
        {
            if (condition)
            {
                Console.WriteLine("  ok    " + description);
                return;
            }

            Console.WriteLine("  FAIL  " + description);
            _failures++;
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch
            {
                // A leftover temp directory is not worth failing the run over.
            }
        }
    }
}
