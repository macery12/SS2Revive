using System;
using System.Collections.Generic;

namespace SS2ReviveData
{
    public struct LocalResponse
    {
        public int Status;
        public string Body;

        public static LocalResponse Ok(Json body) => new LocalResponse { Status = 200, Body = body.ToString() };

        public static LocalResponse Ok(string body) => new LocalResponse { Status = 200, Body = body };

        /// <summary>
        /// 404, never 5xx.
        ///
        /// <c>HttpRequestScheduler.GetResultType</c> maps 2xx to Success, 4xx to Failed, and
        /// everything else - 5xx included - to Retry. A retried request stays in the queue, never
        /// reaches its completion callback, and holds the ServerResource locks it declared, which
        /// blocks every later request touching the same resource. Most call sites leave maxRetries
        /// unset, so that is forever. There is no error this backend can hit that is worth wedging
        /// the client's request pipeline over.
        /// </summary>
        public static LocalResponse Failed(string code) =>
            new LocalResponse { Status = 404, Body = "{\"error\":\"" + code + "\"}" };

        public static LocalResponse BadRequest(string code) =>
            new LocalResponse { Status = 400, Body = "{\"error\":\"" + code + "\"}" };
    }

    /// <summary>Supplies what another player's client published about itself, by whatever means the
    /// host has - see the plugin's SteamPeers, which reads Steam lobby member data and rich
    /// presence. Absence is reported honestly rather than guessed at.</summary>
    public interface IPeerDirectory
    {
        bool TryGetLevel(string playerId, out int seasonLevel, out long seasonXp, out string userName);
    }

    public sealed class LocalBackendOptions
    {
        /// <summary>The game's <c>StreamingAssets/Content/Data</c>, or an install root to search.
        /// Null disables cosmetics rather than guessing at the catalogue.</summary>
        public string ContentDirectory;

        /// <summary>Overrides <see cref="SaveLocation"/>. Empty means use the standard place.</summary>
        public string SaveDirectory;

        /// <summary>See <see cref="PlayerInventory"/> for why this defaults to true.</summary>
        public bool GrantAllCosmetics = true;

        /// <summary>
        /// The signed-in player. Requests about anybody else are answered from
        /// <see cref="LocalBackend.Peers"/> or refused.
        ///
        /// Usually left unset here and supplied afterwards through
        /// <see cref="LocalBackend.LocalPlayerId"/>: a backend embedded in the game is constructed
        /// well before the platform layer has signed anybody in.
        /// </summary>
        public string LocalPlayerId;
    }

    /// <summary>
    /// Bossa's player-progression, challenge and profile services, reimplemented in the process
    /// that used to call them.
    ///
    /// This is the whole serverless design in one class. The plugin already owns every HTTP request
    /// the game makes - it has to, because <c>CrappyHttpsRequestService</c> hardcodes
    /// <c>"https://" + host + path</c> in two places and so cannot be pointed at anything but a
    /// TLS host - and once you own the request, answering it yourself is strictly less machinery
    /// than forwarding it to a server you also have to run.
    ///
    /// What made that practical rather than merely possible is that almost nothing here is shared
    /// state. Daily challenges are seeded from playerId plus the UTC date, so two players never had
    /// the same three; campaign progress is a mirror of a local save; the cosmetics catalogue ships
    /// inside the game as <c>Inventory.dat</c>; and the end-of-match XP screen is computed entirely
    /// client-side from <c>ProgressionConfig.json</c>. The single genuinely cross-player read - a
    /// friend's level - is answered from <see cref="Peers"/>.
    ///
    /// Thread-safety: <see cref="Handle"/> is called from a worker thread and serialises on the
    /// store's gate. <see cref="Peers"/> is read under that gate but is expected to be a snapshot
    /// maintained elsewhere, because Steam's API is main-thread-only.
    /// </summary>
    public sealed class LocalBackend
    {
        private readonly LocalBackendOptions _options;
        private readonly Action<string> _info;
        private readonly Action<string> _warn;
        private readonly LocalStore _store;

        public GameCatalogue Catalogue { get; private set; }

        public string SaveDirectory { get; }

        public IPeerDirectory Peers { get; set; }

        public bool CosmeticsAvailable => Catalogue != null;

        /// <summary>
        /// Who "self" is. Settable, and it matters that it is.
        ///
        /// A host inside the game constructs this during plugin load, which is long before the
        /// game's own platform layer runs <c>SteamAPI.Init</c> - so the identity is not knowable
        /// yet. Getting it wrong is not a cosmetic problem: the client uses one endpoint for both
        /// its own progression and a friend's, distinguished only by the id in the path, so a stale
        /// value here would make the player's own progression look like a stranger's and be
        /// refused.
        /// </summary>
        public string LocalPlayerId
        {
            get { return _options.LocalPlayerId; }
            set { _options.LocalPlayerId = value; }
        }

        public LocalBackend(LocalBackendOptions options, Action<string> info, Action<string> warn)
        {
            _options = options ?? new LocalBackendOptions();
            _info = info ?? delegate { };
            _warn = warn ?? delegate { };

            SaveDirectory = SaveLocation.ResolveDirectory(_options.SaveDirectory);
            _store = new LocalStore(SaveLocation.StateFile(SaveDirectory), _warn);

            LoadCatalogue();
        }

        private void LoadCatalogue()
        {
            var directory = GameCatalogue.FindContentDirectory(_options.ContentDirectory);
            if (directory == null)
            {
                _warn("No Inventory.dat found under '" + (_options.ContentDirectory ?? "<unset>")
                      + "'. Cosmetics stay off; the client keeps whatever it has predicted locally.");
                return;
            }

            try
            {
                Catalogue = GameCatalogue.Load(directory, _warn);
                _info("Cosmetics catalogue: " + Catalogue.Summary() + ".");
            }
            catch (Exception ex)
            {
                // Deliberately not a partial catalogue. See PlayerInventory: an inventory response
                // that omits an item the client owns is a deletion, so a half-read catalogue does
                // real damage where no catalogue at all does none.
                Catalogue = null;
                _warn("Could not read the cosmetics catalogue (" + ex.Message
                      + "). Cosmetics stay off.");
            }
        }

        public string Describe()
        {
            return "state " + _store.FilePath
                   + " | cosmetics " + (Catalogue == null ? "off" : Catalogue.Summary())
                   + (Catalogue != null && _options.GrantAllCosmetics ? " (all granted)" : string.Empty);
        }

        // ------------------------------------------------------------------ sync

        /// <summary>
        /// Adopts the client's own progression as the recorded truth.
        ///
        /// This closes the one hole the HTTP backend could never close. The client persists
        /// <c>ProgressionData</c> to its own file, and <c>RequestProgressionData</c> overwrites
        /// that file with whatever the backend answers - so a backend whose XP total had drifted
        /// low would silently roll the player's level back. The backend cannot avoid drifting,
        /// because <c>CompleteQuickPlayLevel</c> reports only "finished, won or not" and never the
        /// XP, the rating, or campaign rewards.
        ///
        /// In-process there is a better option: hook <c>PersistProgressionData</c> and copy the
        /// number the client just decided on. The two can then never disagree, and the round trip
        /// through this backend becomes an identity function instead of a lossy one.
        /// </summary>
        public void MirrorProgression(string playerId, long seasonXp, int seasonLevel, int globalLevel)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return;

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                if (record.SeasonXp == seasonXp
                    && record.SeasonLevel == seasonLevel
                    && record.GlobalLevel == globalLevel)
                {
                    return;
                }

                record.SeasonXp = seasonXp;
                record.SeasonLevel = seasonLevel;
                record.GlobalLevel = globalLevel;
                record.UpdatedAt = Clock.NowMs();

                if (!_options.GrantAllCosmetics && Catalogue != null)
                {
                    record.Inventory.GrantSets(
                        Catalogue, PlayerInventory.RewardSetsForXp(Catalogue, seasonXp), record.UpdatedAt);
                }

                _store.Save();
            }
        }

        /// <summary>The name to publish for the local player, so a peer's client can show it
        /// without a profile service. Set by the host once Steam has answered.</summary>
        public void SetUserName(string playerId, string userName)
        {
            if (!PlayerIds.IsWellFormed(playerId) || string.IsNullOrEmpty(userName)) return;

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                if (string.Equals(record.UserName, userName, StringComparison.Ordinal)) return;
                record.UserName = userName;
                _store.Save();
            }
        }

        /// <summary>The local player's season level, for publishing to peers.</summary>
        public bool TryGetLocalLevel(out int seasonLevel, out long seasonXp)
        {
            seasonLevel = 1;
            seasonXp = 0;
            if (!PlayerIds.IsWellFormed(_options.LocalPlayerId)) return false;

            lock (_store.Gate)
            {
                var record = _store.Find(_options.LocalPlayerId);
                if (record == null) return false;
                seasonLevel = record.SeasonLevel;
                seasonXp = record.SeasonXp;
                return true;
            }
        }

        /// <summary>The complete local progression tuple, for one-shot repair operations that
        /// must not accidentally lower a global level which differs from the season level.</summary>
        public bool TryGetLocalProgression(out int seasonLevel, out long seasonXp,
                                           out int globalLevel)
        {
            seasonLevel = 1;
            seasonXp = 0;
            globalLevel = 1;
            if (!PlayerIds.IsWellFormed(_options.LocalPlayerId)) return false;

            lock (_store.Gate)
            {
                var record = _store.Find(_options.LocalPlayerId);
                if (record == null) return false;
                seasonLevel = record.SeasonLevel;
                seasonXp = record.SeasonXp;
                globalLevel = record.GlobalLevel;
                return true;
            }
        }

        // ---------------------------------------------------------------- routing

        public LocalResponse Handle(string verb, string path, string body)
        {
            try
            {
                return Route(verb, path, body);
            }
            catch (Exception ex)
            {
                _warn("Local backend threw handling " + verb + " " + path + ": " + ex);
                return LocalResponse.Failed("BACKEND_ERROR");
            }
        }

        private LocalResponse Route(string verb, string path, string body)
        {
            var query = path.IndexOf('?');
            var withoutQuery = query >= 0 ? path.Substring(0, query) : path;
            var segments = withoutQuery.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            var isGet = string.Equals(verb, "GET", StringComparison.OrdinalIgnoreCase);
            var isPost = string.Equals(verb, "POST", StringComparison.OrdinalIgnoreCase);
            var isDelete = string.Equals(verb, "DELETE", StringComparison.OrdinalIgnoreCase);

            // -------------------------------------------------------- housekeeping

            if (isGet && Is(segments, "version"))
            {
                return LocalResponse.Ok(Json.Object()
                    .Add("minimumVersion", "1.3.7.3054")
                    .Add("currentVersion", "1.3.7.3054"));
            }

            if (isGet && Is(segments, "status"))
                return LocalResponse.Ok(Json.Object().Add("maintenanceMode", false));

            if (isGet && Is(segments, "scheduledTimes"))
                return LocalResponse.Ok(Json.Object());

            // The plugin authenticates against Steam and never issues these, but a build with the
            // bypass turned off should still boot rather than hang on a dead login.
            if (isPost && Is(segments, "auth", "authenticate"))
            {
                var playerId = _options.LocalPlayerId;
                if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.Failed("NO_LOCAL_IDENTITY");
                return LocalResponse.Ok(Json.Object()
                    .Add("playerId", playerId)
                    .Add("jwt", "ss2revive-local"));
            }

            if (isPost && Is(segments, "auth", "refreshJWT"))
            {
                return LocalResponse.Ok(Json.Object()
                    .Add("playerId", _options.LocalPlayerId ?? string.Empty)
                    .Add("jwt", "ss2revive-local"));
            }

            // ------------------------------------------------------------ profile

            if (isGet && Is(segments, "profile", "players", "*"))
                return Profile(segments[2]);

            // -------------------------------------------------------- progression

            if (isGet && Is(segments, "player-progression", "playerProgression", "players", "*", "progression"))
                return Progression(segments[3]);

            if (isGet && Is(segments, "player-progression", "players", "*", "campaigns"))
                return Campaigns(segments[2]);

            if (isPost && Is(segments, "player-progression", "campaignLevels", "*", "players", "*", "completed"))
                return CompleteCampaignLevel(segments[2], segments[4], body);

            // UGC is gone, so nobody has favourite levels. LocalUGCLevelCache caches a successful
            // answer for the session and re-asks after every failure, so answering once is cheaper
            // than refusing repeatedly.
            if (isGet && Is(segments, "player-progression", "players", "*", "favouriteLevels"))
                return LocalResponse.Ok("[]");

            // ---------------------------------------------------------- cosmetics

            if (isGet && Is(segments, "player-progression", "itemSets"))
            {
                if (Catalogue == null) return LocalResponse.Failed("CATALOGUE_NOT_LOADED");
                return LocalResponse.Ok(PlayerInventory.ItemSetsResponse(Catalogue));
            }

            if (isPost && Is(segments, "player-progression", "quickplay", "completeLevel"))
                return CompleteQuickPlayLevel(body);

            if (Is(segments, "player-progression", "playerProgression", "players", "*", "inventory"))
            {
                if (Catalogue == null) return LocalResponse.Failed("CATALOGUE_NOT_LOADED");
                if (isGet) return Inventory(segments[3], reset: false);
                if (isDelete) return Inventory(segments[3], reset: true);
            }

            if (isPost && Is(segments, "player-progression", "playerProgression", "players", "*", "addCharacter"))
            {
                if (Catalogue == null) return LocalResponse.Failed("CATALOGUE_NOT_LOADED");
                var parsed = Json.TryParse(body) ?? Json.Object();
                return SetCharacter(segments[3], parsed["characterId"].AsStringOr(string.Empty),
                                    parsed["itemsToEquip"]);
            }

            if (isPost && Is(segments, "player-progression", "playerProgression", "players", "*",
                             "characters", "*", "setEquippedItems"))
            {
                if (Catalogue == null) return LocalResponse.Failed("CATALOGUE_NOT_LOADED");
                var parsed = Json.TryParse(body) ?? Json.Object();
                return SetCharacter(segments[3], segments[5], parsed["equippedItems"]);
            }

            // --------------------------------------------------------- challenges

            if (isGet && Is(segments, "challenges", "challenges", "players", "*", "currentDailyChallenges"))
                return DailyChallengesFor(segments[3]);

            if (isPost && Is(segments, "challenges", "challenges", "players", "*", "challenges", "*"))
                return ApplyChallengeSteps(segments[3], segments[5], body);

            if (isPost && Is(segments, "challenges", "challenges", "forceResetPlayerDailyChallenges"))
                return ResetChallenges(body, expire: false);

            if (isPost && Is(segments, "challenges", "challenges", "expiringPlayerDailyChallenges"))
                return ResetChallenges(body, expire: true);

            return LocalResponse.Failed("ENDPOINT_NOT_IMPLEMENTED");
        }

        /// <summary>"*" matches exactly one segment.</summary>
        private static bool Is(string[] segments, params string[] pattern)
        {
            if (segments.Length != pattern.Length) return false;

            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == "*") continue;
                if (!string.Equals(pattern[i], segments[i], StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        private bool IsLocalPlayer(string playerId) =>
            string.Equals(playerId, _options.LocalPlayerId, StringComparison.Ordinal);

        // --------------------------------------------------------------- handlers

        private LocalResponse Profile(string playerId)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            string userName;
            lock (_store.Gate)
            {
                var record = _store.Find(playerId);
                userName = record != null && record.UserName.Length > 0
                    ? record.UserName
                    : PlayerIds.PlaceholderName(playerId);
            }

            var steamId = PlayerIds.ToSteamId(playerId);
            return LocalResponse.Ok(Json.Object()
                .Add("playerId", playerId)
                .Add("userName", userName)
                .Add("platformName", "steam")
                .Add("platformUserId", steamId == 0UL ? string.Empty : steamId.ToString())
                .Add("profileJson", "{}"));
        }

        /// <summary>
        /// Also serves <c>GetPlayerLevelExperience</c>: the client uses this one path for both its
        /// own progression and a friend's, distinguished only by the id in it.
        ///
        /// For anyone but the local player, the answer comes from what their client published over
        /// Steam. When that is not known, this refuses rather than inventing a level. The failure
        /// callback in <c>PartyNotificationService</c> is an empty method, so refusing costs a
        /// notification toast and nothing else, whereas answering "level 1" for a level 40 friend
        /// is a visible lie - the same reasoning that governs persona names in the profile patch.
        /// </summary>
        private LocalResponse Progression(string playerId)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            if (!IsLocalPlayer(playerId))
            {
                var peers = Peers;
                if (peers == null
                    || !peers.TryGetLevel(playerId, out var peerLevel, out var peerXp, out var peerName))
                {
                    return LocalResponse.Failed("PLAYER_NOT_KNOWN");
                }

                return LocalResponse.Ok(ProgressionJson(playerId,
                    string.IsNullOrEmpty(peerName) ? PlayerIds.PlaceholderName(playerId) : peerName,
                    peerXp, peerLevel, peerLevel));
            }

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                return LocalResponse.Ok(ProgressionJson(playerId, record.UserName,
                    record.SeasonXp, record.SeasonLevel, record.GlobalLevel));
            }
        }

        private static Json ProgressionJson(string playerId, string userName, long seasonXp,
                                            int seasonLevel, int globalLevel)
        {
            // currentGlobalXp mirrors the season total. The client displays the season figures and
            // only reads the global level, so a second independent counter would be invented data.
            return Json.Object()
                .Add("playerId", playerId)
                .Add("userName", userName ?? string.Empty)
                .Add("avatarUrl", string.Empty)
                .Add("profileMetadata", "{}")
                .Add("currentSeasonLevel", seasonLevel)
                .Add("currentSeasonXp", seasonXp)
                .Add("currentGlobalLevel", globalLevel)
                .Add("currentGlobalXp", seasonXp);
        }

        private LocalResponse Campaigns(string playerId)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            lock (_store.Gate)
                return LocalResponse.Ok(_store.GetOrCreate(playerId).Campaigns.ToResponse(playerId));
        }

        private LocalResponse CompleteCampaignLevel(string levelSequenceId, string playerId, string body)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            var parsed = Json.TryParse(body) ?? Json.Object();
            var campaignId = parsed["campaignId"].AsStringOr(string.Empty);
            if (campaignId.Length == 0) return LocalResponse.BadRequest("MISSING_CAMPAIGN_ID");

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                record.Campaigns.Record(campaignId, levelSequenceId,
                    parsed["grade"].AsStringOr("FAIL"),
                    parsed["information"].AsStringOr(string.Empty),
                    Clock.NowMs());
                _store.Save();
            }

            // campaignLevelUnlockedId stays empty: the client unlocks levels from its own campaign
            // data and ignores this response on the normal path - the succeedCallback in
            // UploadCampaignLevelProgress defaults to null.
            return LocalResponse.Ok(Json.Object()
                .Add("playerId", playerId)
                .Add("campaignId", campaignId)
                .Add("campaignLevelSequenceId", levelSequenceId)
                .Add("campaignLevelUnlockedId", Json.Array())
                .Add("setIds", Json.Array()));
        }

        /// <summary>
        /// The end-of-match XP post.
        ///
        /// <c>GrantQuickPlayProgress</c> passes an empty succeed callback and drives its screen
        /// entirely from local data, so nothing here is displayed. It exists to keep the recorded
        /// total moving for a build running with <c>GrantAllCosmetics</c> off - and even then
        /// <see cref="MirrorProgression"/> corrects it moments later, because the client persists
        /// its own figure at the end of the same sequence.
        ///
        /// <c>rated</c> is absent from the request the client sends, so that third of the award
        /// cannot be mirrored here at all. Another reason the client's own number wins.
        /// </summary>
        private LocalResponse CompleteQuickPlayLevel(string body)
        {
            var parsed = Json.TryParse(body) ?? Json.Object();
            var playerId = parsed["playerId"].AsStringOr(string.Empty);
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            var rules = Catalogue?.Experience ?? new ExperienceRules();
            var gained = rules.PlayLevel + (parsed["levelWon"].AsBoolOr(false) ? rules.WinLevel : 0);

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                AwardXp(record, gained);
                _store.Save();

                return LocalResponse.Ok(Json.Object()
                    .Add("seasonId", Catalogue?.SeasonId ?? "1")
                    .Add("playerId", playerId)
                    .Add("seasonLevel", record.SeasonLevel)
                    .Add("seasonExperience", record.SeasonXp));
            }
        }

        /// <summary>Call with the store gate held. Never saves - the caller batches that.</summary>
        private void AwardXp(PlayerRecord record, int amount)
        {
            if (amount <= 0) return;

            record.SeasonXp += amount;
            record.SeasonLevel = PlayerInventory.SeasonLevelForXp(Catalogue, record.SeasonXp);
            record.GlobalLevel = record.SeasonLevel;
            record.UpdatedAt = Clock.NowMs();

            if (!_options.GrantAllCosmetics && Catalogue != null)
            {
                record.Inventory.GrantSets(
                    Catalogue, PlayerInventory.RewardSetsForXp(Catalogue, record.SeasonXp), record.UpdatedAt);
            }
        }

        private LocalResponse Inventory(string playerId, bool reset)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);

                // ResetInventory was a QA tool. It cannot take anything away while everything is
                // granted anyway, so it only clears what this backend recorded.
                if (reset)
                {
                    record.Inventory = new PlayerInventory();
                    _store.Save();
                }

                return LocalResponse.Ok(
                    record.Inventory.ToResponse(playerId, Catalogue, _options.GrantAllCosmetics));
            }
        }

        private LocalResponse SetCharacter(string playerId, string characterId, Json itemIds)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            characterId = (characterId ?? string.Empty).ToLowerInvariant();
            if (characterId.Length == 0) return LocalResponse.BadRequest("MISSING_CHARACTER_ID");

            var requested = new List<string>();
            if (itemIds != null)
            {
                foreach (var value in itemIds.Items) requested.Add(value.AsString());
            }

            CharacterRecord character;
            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                character = record.Inventory.SetCharacter(Catalogue, characterId, requested, Clock.NowMs());
                _store.Save();
            }

            var equipped = Json.Array();
            for (var i = 0; i < character.EquippedItemIds.Count; i++)
                equipped.Add(Json.Str(character.EquippedItemIds[i]));

            return LocalResponse.Ok(Json.Object()
                .Add("playerId", playerId)
                .Add("characterId", characterId)
                .Add("createdAt", character.CreatedAt)
                .Add("equippedItemIds", equipped));
        }

        // --------------------------------------------------------------- challenges

        /// <summary>Call with the store gate held. Rolls a new day when the stored one has
        /// expired, which is also what makes the very first request work.</summary>
        private DailyChallengeRecord EnsureDaily(PlayerRecord record, bool force = false, string seedSuffix = null)
        {
            var now = Clock.NowMs();
            DailyChallenges.CurrentWindow(now, out var dayKey, out _, out _);

            if (!force && record.Challenges != null
                && string.Equals(record.Challenges.DayKey, dayKey, StringComparison.Ordinal))
            {
                return record.Challenges;
            }

            record.Challenges = DailyChallengeRecord.Create(record.PlayerId, now, seedSuffix);
            return record.Challenges;
        }

        private LocalResponse DailyChallengesFor(string playerId)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                var previousDay = record.Challenges?.DayKey;
                var daily = EnsureDaily(record);

                if (!string.Equals(previousDay, daily.DayKey, StringComparison.Ordinal))
                {
                    _info("Rolled daily challenges for " + daily.DayKey + ".");
                    _store.Save();
                }

                return LocalResponse.Ok(daily.ToResponse());
            }
        }

        private LocalResponse ApplyChallengeSteps(string playerId, string challengeId, string body)
        {
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            var parsed = Json.TryParse(body) ?? Json.Object();
            var steps = parsed["stepsCompleted"].AsIntOr(0);

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);
                var daily = EnsureDaily(record);

                if (!daily.ApplySteps(challengeId, steps, out var xpAwarded))
                    return LocalResponse.Failed("CHALLENGE_NOT_FOUND");

                if (xpAwarded > 0)
                {
                    AwardXp(record, xpAwarded);
                    _info("Daily challenge " + challengeId + " completed for " + xpAwarded + " XP.");
                }

                _store.Save();
                return LocalResponse.Ok(daily.ToResponse());
            }
        }

        /// <summary>
        /// Both of these were QA tools on Bossa's side, gated behind a role this backend does not
        /// model. They are kept because they cost nothing and are the only way to exercise the
        /// daily rotation without waiting for UTC midnight.
        /// </summary>
        private LocalResponse ResetChallenges(string body, bool expire)
        {
            var parsed = Json.TryParse(body) ?? Json.Object();
            var playerId = parsed["playerId"].AsStringOr(_options.LocalPlayerId ?? string.Empty);
            if (!PlayerIds.IsWellFormed(playerId)) return LocalResponse.BadRequest("INVALID_PLAYER_ID");

            lock (_store.Gate)
            {
                var record = _store.GetOrCreate(playerId);

                if (expire)
                {
                    record.Challenges = null;
                    _store.Save();
                    return LocalResponse.Ok(Json.Object());
                }

                // A fresh seed, or the reroll would deterministically produce the same three.
                var daily = EnsureDaily(record, force: true, seedSuffix: Clock.NowMs().ToString());
                _store.Save();
                return LocalResponse.Ok(daily.ToResponse());
            }
        }
    }
}
