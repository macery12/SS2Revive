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
                RunDurabilityTests(scratch);
                RunUgcStoreTests(scratch);
                RunLevelSharingTests(scratch);
                RunCommunityCatalogTests();
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

            // A figure the client never sent must stay unknown rather than becoming zero. Zero
            // wins every best-of-lowest comparison there is, so recording one would pin the level
            // at a time nothing could beat and then report that back as a real run.
            Ok(backend, "POST", "/player-progression/campaignLevels/lvl-2/players/" + PlayerA + "/completed",
                "{\"campaignId\":\"c1\",\"grade\":\"PASS\"}");
            var noInformation = FindScore(
                Ok(backend, "GET", "/player-progression/players/" + PlayerA + "/campaigns"), "lvl-2");
            var blank = Json.Parse(noInformation["information"].AsString());
            Check("a completion with no information records no best time",
                blank["bestTime"] == null && blank["bestBloodLoss"] == null);

            Ok(backend, "POST", "/player-progression/campaignLevels/lvl-2/players/" + PlayerA + "/completed",
                "{\"campaignId\":\"c1\",\"grade\":\"PASS\",\"information\":\"{\\\"bestTime\\\":120}\"}");
            var thenTimed = FindScore(
                Ok(backend, "GET", "/player-progression/players/" + PlayerA + "/campaigns"), "lvl-2");
            Check("and a real time afterwards is still able to win",
                Json.Parse(thenTimed["information"].AsString())["bestTime"].AsDouble() == 120);

            // Half a body is the same hazard as none of it.
            Ok(backend, "POST", "/player-progression/campaignLevels/lvl-3/players/" + PlayerA + "/completed",
                "{\"campaignId\":\"c1\",\"grade\":\"PASS\",\"information\":\"{\\\"bestTime\\\":60}\"}");
            var partialInfo = FindScore(
                Ok(backend, "GET", "/player-progression/players/" + PlayerA + "/campaigns"), "lvl-3");
            var half = Json.Parse(partialInfo["information"].AsString());
            Check("one known figure does not fabricate the other",
                half["bestTime"].AsDouble() == 60 && half["bestBloodLoss"] == null);

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

        // ------------------------------------------------------------- durability

        /// <summary>
        /// What happens to a save this build should not touch, and what is left behind when it
        /// touches one it should.
        ///
        /// Both of these are about the same failure: a progress file is the only thing in the mod
        /// that cannot be regenerated from the game's own install, so every path that could damage
        /// one is worth a check that would notice.
        /// </summary>
        private static void RunDurabilityTests(string scratch)
        {
            Section("durability");

            // -- a file from a newer build

            var future = Path.Combine(scratch, "future");
            Directory.CreateDirectory(future);
            var futureFile = SaveLocation.StateFile(future);

            var futureContents =
                "{\"version\":99,\"savedAt\":1,\"players\":[{\"playerId\":\"" + PlayerA
                + "\",\"seasonXp\":999999,\"seasonLevel\":50}]}";
            File.WriteAllText(futureFile, futureContents);

            var warnings = new List<string>();
            var refused = new LocalBackend(new LocalBackendOptions
            {
                SaveDirectory = future,
                LocalPlayerId = PlayerA,
            }, _ => { }, warnings.Add);

            // Searched for rather than counted: this backend has no ContentDirectory, so it also
            // warns about the missing cosmetics catalogue.
            Check("a save from a newer format is refused rather than misread",
                warnings.Exists(w => w.Contains("newer version") && w.Contains("99")));

            var fresh = Ok(refused, "GET",
                "/player-progression/playerProgression/players/" + PlayerA + "/progression");
            Check("and the session starts from empty instead", fresh["currentSeasonXp"].AsLong() == 0);

            // The point of refusing: playing on must not overwrite what could not be read.
            refused.MirrorProgression(PlayerA, 10, 2, 2);
            Check("and playing on does not overwrite it",
                File.ReadAllText(futureFile) == futureContents);

            // -- the backup the atomic write leaves behind

            var live = Path.Combine(scratch, "durable");
            var backend = new LocalBackend(new LocalBackendOptions
            {
                SaveDirectory = live,
                LocalPlayerId = PlayerA,
            }, _ => { }, _ => { });

            var stateFile = SaveLocation.StateFile(live);

            backend.MirrorProgression(PlayerA, 100, 2, 2);
            Check("the first save writes the file", File.Exists(stateFile));
            Check("with nothing displaced, there is no backup yet",
                !File.Exists(stateFile + ".bak"));

            backend.MirrorProgression(PlayerA, 200, 3, 3);
            Check("the second save keeps the copy it replaced", File.Exists(stateFile + ".bak"));

            var current = Json.Parse(File.ReadAllText(stateFile));
            var previous = Json.Parse(File.ReadAllText(stateFile + ".bak"));
            Check("the live file is the newer state",
                current["players"][0]["seasonXp"].AsLong() == 200);
            Check("and the backup is the older one, intact and parseable",
                previous["players"][0]["seasonXp"].AsLong() == 100);

            // No temporary file may survive a completed write, or the next one resumes from it.
            Check("no .tmp is left behind", !File.Exists(stateFile + ".tmp"));
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

        private static Json FindScore(Json response, string levelSequenceId)
        {
            foreach (var score in response["scores"].Items)
            {
                if (score["campaignLevelSequenceId"].AsString() == levelSequenceId) return score;
            }
            throw new InvalidOperationException("Level " + levelSequenceId + " missing from the mirror.");
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

        // ------------------------------------------------------------ level sharing

        private static void RunLevelSharingTests(string scratch)
        {
            Section("share codes");

            // Fixed vector rather than a round trip alone. This encoding has to stay identical to
            // UGCService2's, because the game's own search box decodes what we encode - and this
            // particular id produces a '/' in its base64, which is the character that has to become
            // an underscore.
            const string knownId = "1a658233-92c5-4b63-87fc-4740c855730b";
            const string knownCode = "M4JlGsWSY0uH_EdAyFVzCw";

            Check("a level id encodes to the code the game would show",
                LevelCode.FromLevelId(knownId) == knownCode);

            string decoded;
            Check("and that code decodes back to the id",
                LevelCode.TryToLevelId(knownCode, out decoded) && decoded == knownId);

            Check("codes are always 22 characters", LevelCode.FromLevelId(knownId).Length == LevelCode.Length);
            Check("a code of the wrong length is refused", !LevelCode.TryToLevelId("tooshort", out decoded));
            Check("22 characters of nonsense is refused",
                !LevelCode.TryToLevelId("!!!!!!!!!!!!!!!!!!!!!!", out decoded));
            Check("an id that is not a guid has no code", LevelCode.FromLevelId("not-a-guid") == string.Empty);

            Section("level bundles");

            var root = Path.Combine(scratch, "sharing");
            var store = new UgcStore(root);

            // Real levels start with the game's file magic, and the reader refuses anything that
            // does not - so a bundle test that skipped it would be testing the wrong thing.
            var content = LevelBytes(29, 4096);
            var image = GameImageBytes(64);
            var thumbnail = GameImageBytes(128);

            var level = store.Create("Kidney Trouble", "mind the ribs",
                new List<string> { PlayerA }, new List<string> { "TEAM_COOP" },
                29, content, image);
            store.AddImage(level, thumbnail, PlayerA, true);

            string error;
            var bundle = LevelBundle.FromLevel(level, store.ReadKey, out error);
            Check("a level packs into a bundle", bundle != null && error == null);

            Check("the bundle's file name carries the code",
                bundle.SuggestedFileName() == "Kidney Trouble [" + bundle.Code + "]" + LevelBundle.Extension);

            var packed = bundle.Pack();
            var read = LevelBundle.Unpack(packed, out error);

            Check("a bundle round trips", read != null && error == null);
            Check("the id survives, which is what keeps the code stable", read.Id == level.Id);
            Check("the title survives", read.Title == "Kidney Trouble");
            Check("the description survives", read.Description == "mind the ribs");
            Check("the creator survives", read.CreatorIds.Contains(PlayerA));
            Check("tags survive", read.Tags.Contains("TEAM_COOP"));
            Check("the client version survives", read.ClientVersion == 29);
            Check("the level data survives byte for byte", BytesEqual(read.Content, content));
            Check("the screenshot survives", BytesEqual(read.ContentImage, image));
            Check("the thumbnail survives", BytesEqual(read.Thumbnail, thumbnail));
            Check("the code is the same on both sides", read.Code == bundle.Code);

            // Everything below is a file that arrived from somebody else.
            Check("an empty file is refused", LevelBundle.Unpack(new byte[0], out error) == null);
            Check("a file that is not a bundle is refused",
                LevelBundle.Unpack(new byte[64], out error) == null);

            var truncated = new byte[packed.Length / 2];
            Buffer.BlockCopy(packed, 0, truncated, 0, truncated.Length);
            Check("a half-downloaded bundle is refused", LevelBundle.Unpack(truncated, out error) == null);

            var tampered = (byte[])packed.Clone();
            var payloadOffset = FindBytes(tampered, content);
            if (payloadOffset >= 0) tampered[payloadOffset + 32] ^= 0x5A;
            Check("a bundle whose level bytes do not match its checksum is refused",
                payloadOffset >= 0 && LevelBundle.Unpack(tampered, out error) == null);

            // The one number in the format that sizes an allocation. A reader that believed it
            // would try for two gigabytes on a file of a few kilobytes.
            var lying = OverstatedEntryLength(packed);
            Check("an entry claiming to be larger than the file is refused",
                LevelBundle.Unpack(lying, out error) == null);

            var notALevel = new LevelBundle
            {
                Id = level.Id,
                Title = "Nope",
                Content = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
            };
            Check("a bundle whose payload is not a level is refused",
                LevelBundle.Unpack(notALevel.Pack(), out error) == null);

            var badImages = new LevelBundle
            {
                Id = level.Id,
                Title = "Odd pictures",
                Content = content,
                ContentImage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                Thumbnail = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            };
            var cleaned = LevelBundle.Unpack(badImages.Pack(), out error);
            Check("a bundle with invalid image envelopes still imports", cleaned != null);
            Check("but the images are dropped rather than passed on",
                cleaned.ContentImage == null && cleaned.Thumbnail == null);
            Check("undefined texture formats are refused",
                !LevelBundle.IsSafeRawGameImage(16, 16, 0, 256)
                && !LevelBundle.IsSafeRawGameImage(16, 16, 6, 1024)
                && !LevelBundle.IsSafeRawGameImage(16, 16, 8, 1024));
            Check("raw image data must contain its declared texture",
                LevelBundle.IsSafeRawGameImage(16, 16, 4, 1024)
                && !LevelBundle.IsSafeRawGameImage(2048, 2048, 4, 16));

            Section("importing");

            var receiving = new UgcStore(Path.Combine(scratch, "receiving"));

            var adopted = receiving.Adopt(Prototype(read), read.ClientVersion, read.Content,
                                          read.ContentImage, read.Thumbnail, out error);

            Check("a bundle imports into an empty library", adopted != null && error == null);
            Check("it keeps the id it arrived with", adopted.Id == level.Id);
            Check("so the author's code finds it here too",
                LevelCode.TryToLevelId(bundle.Code, out decoded) && receiving.Get(decoded) != null);
            Check("it arrives published, so it shows in Discover",
                adopted.Status == UgcStore.StatusPublished);
            Check("it stays credited to whoever built it", adopted.CreatorIds.Contains(PlayerA));
            Check("its level data is readable through the store",
                BytesEqual(receiving.ReadKey(adopted.LatestContent().Key), content));
            Check("it has a thumbnail to show", !string.IsNullOrEmpty(adopted.ThumbnailKey));

            int pages;
            var found = receiving.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                Ids = new List<string> { decoded },
            }, out pages);
            Check("a code search finds exactly the imported level",
                found.Count == 1 && found[0].Id == level.Id);

            var mine = receiving.Search(new UgcQuery
            {
                Status = UgcStore.StatusPublished,
                CreatorId = PlayerB,
            }, out pages);
            Check("and it is not listed as the importer's own level", mine.Count == 0);

            Check("importing the same level twice says so rather than duplicating it",
                receiving.Adopt(Prototype(read), 29, content, null, null, out error) == null
                && error == "already-here");

            var updatedContent = LevelBytes(29, 6144);
            UgcInstallOutcome outcome;
            var updated = receiving.Install(Prototype(read), 29, 2, UgcStore.NowMs(),
                updatedContent, null, null, out outcome, out error);
            Check("a newer shared revision updates the installed map in place",
                updated != null && outcome == UgcInstallOutcome.Updated && updated.Id == level.Id);
            Check("the newer revision replaces what play will load",
                updated.LatestContent().ContentVersion == 2
                && BytesEqual(receiving.ReadKey(updated.LatestContent().Key), updatedContent));

            receiving.Install(Prototype(read), 29, 1, UgcStore.NowMs(), content, null, null,
                              out outcome, out error);
            Check("an older shared revision is ignored", outcome == UgcInstallOutcome.Older
                && receiving.Get(level.Id).LatestContent().ContentVersion == 2);

            receiving.Install(Prototype(read), 29, 2, UgcStore.NowMs(), updatedContent, null, null,
                              out outcome, out error);
            Check("reinstalling identical current bytes is idempotent",
                outcome == UgcInstallOutcome.Current);

            var conflictingContent = LevelBytes(29, 7168);
            receiving.Install(Prototype(read), 29, 2, UgcStore.NowMs(), conflictingContent,
                              null, null, out outcome, out error);
            Check("the same revision with different bytes is refused",
                outcome == UgcInstallOutcome.Conflict && !string.IsNullOrEmpty(error));

            var forged = Prototype(read);
            forged.Id = "../../../etc/passwd";
            Check("an id that is not a guid is refused, because it would be a folder name",
                receiving.Adopt(forged, 29, content, null, null, out error) == null
                && error != "already-here");

            var reopened = new UgcStore(Path.Combine(scratch, "receiving"));
            var survived = reopened.Get(level.Id);
            Check("imported levels survive a restart", survived != null);
            Check("with their creator intact", survived != null && survived.CreatorIds.Contains(PlayerA));

            Section("level store containment");

            var containmentRoot = Path.Combine(scratch, "containment");
            var levelsRoot = Path.Combine(containmentRoot, "levels");
            var folderId = Guid.NewGuid().ToString();
            var maliciousFolder = Path.Combine(levelsRoot, folderId);
            var sentinel = Path.Combine(scratch, "sentinel");
            Directory.CreateDirectory(maliciousFolder);
            Directory.CreateDirectory(sentinel);
            File.WriteAllText(Path.Combine(sentinel, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(maliciousFolder, "asset.json"),
                "{\"version\":1,\"id\":\"../../sentinel\",\"title\":\"bad\"}");

            var warnings = new List<string>();
            var contained = new UgcStore(containmentRoot, warnings.Add);
            Check("metadata whose id disagrees with its GUID folder is not loaded",
                contained.Count == 0 && warnings.Count > 0);
            Check("and cannot turn map deletion into recursive path traversal",
                File.Exists(Path.Combine(sentinel, "keep.txt")));
        }

        private static UgcLevelRecord Prototype(LevelBundle bundle) => new UgcLevelRecord
        {
            Id = bundle.Id,
            Title = bundle.Title,
            Description = bundle.Description,
            CreatorIds = new List<string>(bundle.CreatorIds),
            Tags = new List<string>(bundle.Tags),
            CreatedAtMs = bundle.CreatedAtMs,
            Configurations = bundle.Configurations,
            Validations = bundle.Validations,
        };

        // ------------------------------------------------------ community catalogue

        private static void RunCommunityCatalogTests()
        {
            Section("community catalogue");
            const string id = "1a658233-92c5-4b63-87fc-4740c855730b";
            var sha = new string('a', 64);
            var json = "{\"schemaVersion\":1,\"generatedAtUtc\":\"2026-08-08T00:00:00Z\",\"maps\":[{"
                + "\"id\":\"" + id + "\",\"code\":\"M4JlGsWSY0uH_EdAyFVzCw\","
                + "\"revision\":3,\"title\":\"Kidney Trouble\",\"description\":\"Safe & curated\","
                + "\"creatorIds\":[\"" + PlayerA + "\"],\"tags\":[\"TEAM_COOP\"],"
                + "\"createdAtMs\":100,\"updatedAtMs\":200,\"clientVersion\":29,"
                + "\"mapFormatVersion\":29,\"minimumReviveVersion\":\"1.1.0\","
                + "\"sizeBytes\":4096,\"sha256\":\"" + sha + "\","
                + "\"bundleKey\":\"maps/kidney-r3.ss2level\","
                + "\"thumbnailSizeBytes\":1024,\"thumbnailSha256\":\"" + sha + "\","
                + "\"thumbnailKey\":\"thumbs/kidney-r3.bin\","
                + "\"configurations\":[{\"numberPlayers\":2}],\"validations\":[]}]}";

            CommunityCatalog catalog;
            string error;
            Check("a valid API catalogue parses",
                CommunityCatalog.TryParse(System.Text.Encoding.UTF8.GetBytes(json), out catalog, out error)
                && catalog.Entries.Count == 1);
            var extremeDates = json.Replace("\"createdAtMs\":100,\"updatedAtMs\":200",
                "\"createdAtMs\":9223372036854775807,\"updatedAtMs\":9223372036854775807");
            CommunityCatalog dated;
            Check("hostile timestamps are clamped before game DateTime conversion",
                CommunityCatalog.TryParse(System.Text.Encoding.UTF8.GetBytes(extremeDates),
                                          out dated, out error)
                && dated.Entries.Count == 1
                && dated.Entries[0].CreatedAtMs <= UgcStore.NowMs() + 24L * 60 * 60 * 1000
                && dated.Entries[0].UpdatedAtMs <= UgcStore.NowMs() + 24L * 60 * 60 * 1000);
            Check("relative nested object keys are accepted",
                CommunityCatalog.IsSafeObjectKey("maps/2026/map.ss2level"));
            Check("absolute and traversal object keys are refused",
                !CommunityCatalog.IsSafeObjectKey("https://attacker.invalid/map")
                && !CommunityCatalog.IsSafeObjectKey("../map.ss2level")
                && !CommunityCatalog.IsSafeObjectKey("maps/%2e%2e/secret"));
            Check("a catalogue map is filtered by party size",
                catalog.Search(new UgcQuery { Status = UgcStore.StatusPublished, PartySize = 2 }).Count == 1
                && catalog.Search(new UgcQuery { Status = UgcStore.StatusPublished, PartySize = 4 }).Count == 0);
            Check("remote attribution never grants My Levels ownership",
                catalog.Search(new UgcQuery { AnyStatus = true, CreatorId = PlayerA }).Count == 0);
            Check("the current map and mod versions are compatible",
                CommunityCatalog.CompatibilityError(catalog.Entries[0], "1.1.0") == null);
            catalog.Entries[0].MinimumReviveVersion = "2.0.0";
            Check("a newer minimum mod version is rejected with a reason",
                CommunityCatalog.CompatibilityError(catalog.Entries[0], "1.1.0").Contains("Requires"));
            catalog.Entries[0].MinimumReviveVersion = "1.0.0";
            catalog.Entries[0].MapFormatVersion = 30;
            Check("a future map format is rejected with a reason",
                CommunityCatalog.CompatibilityError(catalog.Entries[0], "1.1.0").Contains("Map format"));

            var extremeTimestamp = json.Replace("1700000000000", "9223372036854775807");
            Check("catalogue timestamps are clamped before terminal date conversion",
                CommunityCatalog.TryParse(System.Text.Encoding.UTF8.GetBytes(extremeTimestamp),
                                          out catalog, out error)
                && catalog.Entries[0].CreatedAtMs <= UgcStore.NowMs() + 24L * 60 * 60 * 1000
                && catalog.Entries[0].UpdatedAtMs <= UgcStore.NowMs() + 24L * 60 * 60 * 1000);

            var oversized = new byte[CommunityCatalog.MaxDocumentBytes + 1];
            Check("an oversized catalogue is refused before JSON parsing",
                !CommunityCatalog.TryParse(oversized, out catalog, out error));
            var deep = new string('[', 65) + new string(']', 65);
            Check("deeply nested JSON is refused before recursive parsing",
                !CommunityCatalog.TryParse(System.Text.Encoding.UTF8.GetBytes(deep),
                                           out catalog, out error)
                && error.Contains("deeply"));
        }

        /// <summary>Bytes shaped like a level file: the game's magic, a format version, then filler.</summary>
        private static byte[] LevelBytes(int formatVersion, int length)
        {
            var bytes = new byte[length];
            var magic = new byte[] { 83, 117, 114, 103, 101, 111, 110, 115 };
            Buffer.BlockCopy(magic, 0, bytes, 0, magic.Length);
            bytes[8] = (byte)(formatVersion & 0xFF);
            bytes[9] = (byte)((formatVersion >> 8) & 0xFF);
            for (var i = 10; i < length; i++) bytes[i] = (byte)(i * 31);
            return bytes;
        }

        private static byte[] GameImageBytes(int dataLength)
        {
            var bit = 0;
            var totalBits = 104 + dataLength * 8;
            var words = new uint[(totalBits + 31) / 32];

            WriteImageBits(words, ref bit, (uint)Math.Max(1, dataLength / 4), 31);
            WriteImageBits(words, ref bit, 1, 31);
            WriteImageBits(words, ref bit, 4, 4); // TextureFormat.RGBA32: four bytes per pixel.
            WriteImageBits(words, ref bit, (uint)dataLength, 31);
            bit = (bit + 7) & ~7;
            for (var i = 0; i < dataLength; i++)
                WriteImageBits(words, ref bit, (uint)(i * 31), 8);

            var bytes = new byte[(bit + 7) / 8];
            for (var i = 0; i < words.Length; i++)
            {
                var offset = i * 4;
                if (offset < bytes.Length) bytes[offset] = (byte)words[i];
                if (offset + 1 < bytes.Length) bytes[offset + 1] = (byte)(words[i] >> 8);
                if (offset + 2 < bytes.Length) bytes[offset + 2] = (byte)(words[i] >> 16);
                if (offset + 3 < bytes.Length) bytes[offset + 3] = (byte)(words[i] >> 24);
            }
            return bytes;
        }

        private static void WriteImageBits(uint[] words, ref int bitOffset, uint value, int count)
        {
            var written = 0;
            while (written < count)
            {
                var word = bitOffset / 32;
                var within = bitOffset % 32;
                var take = Math.Min(count - written, 32 - within);
                var mask = take == 32 ? uint.MaxValue : (1u << take) - 1u;
                words[word] |= ((value >> written) & mask) << within;
                written += take;
                bitOffset += take;
            }
        }

        /// <summary>
        /// Rewrites the first entry's length field to int.MaxValue, leaving everything else alone.
        /// The layout is magic, version, then name length, name, payload length - so the field sits
        /// immediately after the first entry's name.
        /// </summary>
        private static byte[] OverstatedEntryLength(byte[] packed)
        {
            var copy = (byte[])packed.Clone();
            var offset = 16 + 2;
            var nameLength = copy[offset] | (copy[offset + 1] << 8);
            offset += 2 + nameLength;

            copy[offset] = 0xFF;
            copy[offset + 1] = 0xFF;
            copy[offset + 2] = 0xFF;
            copy[offset + 3] = 0x7F;
            return copy;
        }

        private static int FindBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0
                || needle.Length > haystack.Length) return -1;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] == needle[j]) continue;
                    match = false;
                    break;
                }
                if (match) return i;
            }
            return -1;
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
