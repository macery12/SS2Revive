using System;
using System.Collections.Generic;

namespace SS2ReviveData
{
    public sealed class ChallengeDefinition
    {
        public string Id;
        public int TotalNumberOfSteps;
        public int XpGain;

        /// <summary>
        /// The <c>metadata</c> string, already serialised. It is a JSON document that travels
        /// inside a JSON string value, and the client feeds it to <c>Services.ChallengeConfig</c>
        /// to decide which in-game statistics count towards the challenge.
        /// </summary>
        public string Metadata;
    }

    /// <summary>
    /// Daily challenges.
    ///
    /// The client does all the presentation. What it needs from a server is only: which three
    /// challenges a player has today, how far through each one they are, and the metadata that
    /// says what counts.
    ///
    /// Constraints that come from the client and are not negotiable:
    ///
    /// - <c>ChallengeService.GetDailyChallenges</c> rejects any response that does not contain
    ///   exactly three challenges, and <c>PopulateChallengeData</c> then indexes [0..2] directly.
    ///   <see cref="PerDay"/> is not a tuning knob.
    /// - <c>Challenge.FormatString</c> logs an error when a description contains <c>{1}</c>
    ///   without <c>AssociatedPropGuid</c>, <c>{2}</c> without <c>MinimumPartySize</c>, or
    ///   <c>{3}</c> without <c>RequiredLimbCategory</c>. Every entry below is paired with a
    ///   description whose placeholders it actually satisfies.
    /// - The localisation keys are the ones shipped in the game's own
    ///   <c>StreamingAssets/Localization/Localization.csv</c>. An unknown key renders as an empty
    ///   string, so inventing keys is not an option.
    ///
    /// Challenges whose descriptions need an <c>AssociatedPropGuid</c> - the per-organ and per-prop
    /// "Perform {0} {1} transplants" variants - are deliberately absent. Those need the prop GUID
    /// catalogue out of the asset bundles, which is a different table from the one in Inventory.dat,
    /// and a wrong GUID silently never matches.
    /// </summary>
    public static class DailyChallenges
    {
        public const int PerDay = 3;

        private const long DayMs = 24L * 60 * 60 * 1000;

        private static readonly ChallengeDefinition[] Catalogue =
        {
            Define("dc-finish-matches", 3, 150,
                "{\"LevelStatisticType\":\"FinishQuickplayMatch\","
                + "\"NameLocalisationKey\":\"FINISH_QUICKPLAY_MATCH\","
                + "\"DescriptionLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_DESCRIPTION\"}"),

            Define("dc-finish-party-2", 2, 200,
                "{\"LevelStatisticType\":\"FinishQuickplayMatch\",\"MinimumPartySize\":2,"
                + "\"NameLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_WITH_2\","
                + "\"DescriptionLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_WITH_N_DESCRIPTION\"}"),

            Define("dc-finish-party-3", 2, 250,
                "{\"LevelStatisticType\":\"FinishQuickplayMatch\",\"MinimumPartySize\":3,"
                + "\"NameLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_WITH_3\","
                + "\"DescriptionLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_WITH_N_DESCRIPTION\"}"),

            Define("dc-finish-strangers", 2, 200,
                "{\"LevelStatisticType\":\"FinishQuickplayMatchWithStrangers\","
                + "\"NameLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_WITH_STRANGERS\","
                + "\"DescriptionLocalisationKey\":\"FINISH_QUICKPLAY_MATCH_WITH_STRANGERS_DESCRIPTION\"}"),

            Define("dc-win-matches", 2, 200,
                "{\"LevelStatisticType\":\"WinQuickplayMatch\","
                + "\"NameLocalisationKey\":\"WIN_QUICKPLAY_MATCH\","
                + "\"DescriptionLocalisationKey\":\"WIN_QUICKPLAY_MATCH_DESCRIPTION\"}"),

            Define("dc-rate-levels", 3, 100,
                "{\"LevelStatisticType\":\"RateQuickplayMatch\","
                + "\"NameLocalisationKey\":\"RATE_QUICKPLAY_MATCH\","
                + "\"DescriptionLocalisationKey\":\"RATE_QUICKPLAY_MATCH_DESCRIPTION\"}"),

            Define("dc-transplant-organs", 5, 150,
                "{\"LevelStatisticType\":\"TransplantHealthyOrgan\","
                + "\"NameLocalisationKey\":\"TRANSPLANT_ORGANS\","
                + "\"DescriptionLocalisationKey\":\"TRANSPLANT_ORGANS_DESCRIPTION\"}"),

            Define("dc-transplant-limbs", 4, 150,
                "{\"LevelStatisticType\":\"TransplantHealthyLimb\","
                + "\"NameLocalisationKey\":\"TRANSPLANT_LIMBS\","
                + "\"DescriptionLocalisationKey\":\"TRANSPLANT_LIMBS_DESCRIPTION\"}"),

            Define("dc-transplant-legs", 3, 175,
                "{\"LevelStatisticType\":\"TransplantHealthyLimb\",\"RequiredLimbCategory\":\"Leg\","
                + "\"NameLocalisationKey\":\"TRANSPLANT_LEGS\","
                + "\"DescriptionLocalisationKey\":\"TRANSPLANT_SPECIFIC_LIMBS_DESCRIPTION\"}"),

            Define("dc-transplant-arms", 3, 175,
                "{\"LevelStatisticType\":\"TransplantHealthyLimb\",\"RequiredLimbCategory\":\"Arm\","
                + "\"NameLocalisationKey\":\"TRANSPLANT_ARMS\","
                + "\"DescriptionLocalisationKey\":\"TRANSPLANT_SPECIFIC_LIMBS_DESCRIPTION\"}"),

            // Syringe.Use accumulates capacity and reports whole millilitres, so the step count
            // here is millilitres injected, not injections.
            Define("dc-inject-blood", 300, 125,
                "{\"LevelStatisticType\":\"UseSyringe\","
                + "\"NameLocalisationKey\":\"INJECT_BLOOD\","
                + "\"DescriptionLocalisationKey\":\"INJECT_BLOOD_DESCRIPTION\"}"),
        };

        private static ChallengeDefinition Define(string id, int steps, int xp, string metadata) =>
            new ChallengeDefinition { Id = id, TotalNumberOfSteps = steps, XpGain = xp, Metadata = metadata };

        public static int CatalogueSize => Catalogue.Length;

        // ------------------------------------------------------------ day window

        /// <summary>UTC midnight to UTC midnight, which is what the client's expiry countdown
        /// assumes.</summary>
        public static void CurrentWindow(long nowMs, out string dayKey, out long createdAt, out long expiresAt)
        {
            createdAt = nowMs - Mod(nowMs, DayMs);
            expiresAt = createdAt + DayMs;
            dayKey = Epoch.AddMilliseconds(createdAt).ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>C# '%' keeps the sign of the dividend; this does not. Only reachable for
        /// pre-1970 clocks, but a negative offset there would shift the whole day window.</summary>
        private static long Mod(long value, long modulus)
        {
            var remainder = value % modulus;
            return remainder < 0 ? remainder + modulus : remainder;
        }

        // ---------------------------------------------------------------- rolling

        private static uint Fnv1a(string text)
        {
            var hash = 0x811C9DC5u;
            for (var i = 0; i < text.Length; i++)
            {
                hash ^= (uint)text[i];
                hash *= 0x01000193u;
            }
            return hash;
        }

        /// <summary>
        /// Deterministic per player per day.
        ///
        /// This is the property that made daily challenges a poor fit for a shared server in the
        /// first place, and it is why losing the server costs nothing here: two players never had
        /// the same three challenges, so there was never any shared state to lose. It also means a
        /// reinstall hands back the same set rather than rerolling a half-finished day.
        /// </summary>
        public static List<ChallengeProgress> Roll(string playerId, string seed)
        {
            var pool = new List<ChallengeDefinition>(Catalogue);
            var picked = new List<ChallengeProgress>(PerDay);
            var state = Fnv1a(playerId + "|" + seed);

            while (picked.Count < PerDay && pool.Count > 0)
            {
                state = (state ^ (uint)picked.Count) * 0x01000193u;
                var index = (int)(state % (uint)pool.Count);
                var entry = pool[index];
                pool.RemoveAt(index);

                picked.Add(new ChallengeProgress
                {
                    ChallengeId = entry.Id,
                    Metadata = entry.Metadata,
                    TotalNumberOfSteps = entry.TotalNumberOfSteps,
                    StepsCompleted = 0,
                    XpGain = entry.XpGain,
                });
            }

            return picked;
        }
    }

    public sealed class ChallengeProgress
    {
        public string ChallengeId;
        public string Metadata;
        public int TotalNumberOfSteps;
        public int StepsCompleted;
        public int XpGain;

        public Json ToJson()
        {
            return Json.Object()
                .Add("challengeId", ChallengeId)
                .Add("metadata", Metadata)
                .Add("totalNumberOfSteps", TotalNumberOfSteps)
                .Add("stepsCompleted", StepsCompleted)
                .Add("xpGain", XpGain);
        }

        public static ChallengeProgress FromJson(Json value)
        {
            return new ChallengeProgress
            {
                ChallengeId = value["challengeId"].AsStringOr(string.Empty),
                Metadata = value["metadata"].AsStringOr("{}"),
                TotalNumberOfSteps = value["totalNumberOfSteps"].AsIntOr(1),
                StepsCompleted = value["stepsCompleted"].AsIntOr(0),
                XpGain = value["xpGain"].AsIntOr(0),
            };
        }
    }

    /// <summary>One player's challenges for one UTC day.</summary>
    public sealed class DailyChallengeRecord
    {
        public string PlayerId;
        public string DayKey;
        public long CreatedAt;
        public long ExpiresAt;
        public List<ChallengeProgress> Challenges = new List<ChallengeProgress>();

        /// <summary>Ids whose completion XP has already been paid, so a repeated upload of the
        /// same final step cannot be paid twice.</summary>
        public List<string> AwardedXpFor = new List<string>();

        public static DailyChallengeRecord Create(string playerId, long nowMs, string seedSuffix = null)
        {
            DailyChallenges.CurrentWindow(nowMs, out var dayKey, out var createdAt, out var expiresAt);
            var seed = seedSuffix == null ? dayKey : dayKey + "|" + seedSuffix;

            return new DailyChallengeRecord
            {
                PlayerId = playerId,
                DayKey = dayKey,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
                Challenges = DailyChallenges.Roll(playerId, seed),
            };
        }

        /// <summary>
        /// <c>stepsCompleted</c> from the client is an absolute total, not a delta, so this clamps
        /// into range and refuses to move backwards - a late-arriving retry carrying an older value
        /// must not undo progress.
        ///
        /// Returns the XP earned by a challenge that crossed into completion on this call, which
        /// the caller adds to the player's progression.
        /// </summary>
        public bool ApplySteps(string challengeId, int stepsCompleted, out int xpAwarded)
        {
            xpAwarded = 0;

            ChallengeProgress challenge = null;
            for (var i = 0; i < Challenges.Count; i++)
            {
                if (!string.Equals(Challenges[i].ChallengeId, challengeId, StringComparison.Ordinal)) continue;
                challenge = Challenges[i];
                break;
            }

            if (challenge == null) return false;

            var clamped = Math.Min(Math.Max(stepsCompleted, 0), challenge.TotalNumberOfSteps);
            challenge.StepsCompleted = Math.Max(challenge.StepsCompleted, clamped);

            if (challenge.StepsCompleted >= challenge.TotalNumberOfSteps
                && !AwardedXpFor.Contains(challengeId))
            {
                AwardedXpFor.Add(challengeId);
                xpAwarded = challenge.XpGain;
            }

            return true;
        }

        /// <summary>The exact shape <c>ChallengeApi.GetDailyChallengeResponse</c> reads. Extra keys
        /// are ignored by it, missing ones are not.</summary>
        public Json ToResponse()
        {
            var challenges = Json.Array();
            for (var i = 0; i < Challenges.Count; i++)
                challenges.Add(Challenges[i].ToJson());

            return Json.Object()
                .Add("playerId", PlayerId)
                .Add("createdAt", CreatedAt)
                .Add("expiresAt", ExpiresAt)
                .Add("challenges", challenges);
        }

        public Json ToStorage()
        {
            var challenges = Json.Array();
            for (var i = 0; i < Challenges.Count; i++)
                challenges.Add(Challenges[i].ToJson());

            var awarded = Json.Array();
            for (var i = 0; i < AwardedXpFor.Count; i++)
                awarded.Add(Json.Str(AwardedXpFor[i]));

            return Json.Object()
                .Add("playerId", PlayerId)
                .Add("dayKey", DayKey)
                .Add("createdAt", CreatedAt)
                .Add("expiresAt", ExpiresAt)
                .Add("challenges", challenges)
                .Add("awardedXpFor", awarded);
        }

        public static DailyChallengeRecord FromStorage(Json value)
        {
            var record = new DailyChallengeRecord
            {
                PlayerId = value["playerId"].AsStringOr(string.Empty),
                DayKey = value["dayKey"].AsStringOr(string.Empty),
                CreatedAt = value["createdAt"].AsLongOr(0),
                ExpiresAt = value["expiresAt"].AsLongOr(0),
            };

            var challenges = value["challenges"];
            if (challenges != null)
            {
                foreach (var entry in challenges.Items)
                    record.Challenges.Add(ChallengeProgress.FromJson(entry));
            }

            var awarded = value["awardedXpFor"];
            if (awarded != null)
            {
                foreach (var entry in awarded.Items)
                    record.AwardedXpFor.Add(entry.AsString());
            }

            return record;
        }
    }
}
