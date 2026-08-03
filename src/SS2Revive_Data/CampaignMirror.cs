using System;
using System.Collections.Generic;

namespace SS2ReviveData
{
    public sealed class CampaignScore
    {
        public string CampaignId;
        public string CampaignLevelSequenceId;
        public string Grade;
        public long CreatedAt;

        /// <summary>
        /// Positive infinity means "never learned", not zero.
        ///
        /// This distinction is the whole point. Both of these are best-of-lowest values, so a
        /// figure the client never sent has to lose every comparison it takes part in - and zero
        /// wins all of them. Storing an absent time as 0 would take the first completion that
        /// arrived without an <c>information</c> body and pin the level's best time at zero
        /// forever: no later real time could beat it, and the mirror would then report a
        /// zero-second run back to a client whose merge rule is "take the server's if it is
        /// lower". Infinity loses instead, which is what "unknown" is supposed to do.
        ///
        /// <see cref="CampaignMirror.ToResponse"/> and <see cref="CampaignMirror.ToStorage"/> omit
        /// these keys entirely while they are infinite, so an unknown value is never asserted as a
        /// number anywhere it could be read back as one.
        /// </summary>
        public double BestTime = double.PositiveInfinity;

        /// <summary>See <see cref="BestTime"/>.</summary>
        public double BestBloodLoss = double.PositiveInfinity;
    }

    /// <summary>
    /// The campaign progress mirror.
    ///
    /// This is not the campaign save. <c>CampaignPlayerProgressLoader.CompleteCampaignLevel</c>
    /// writes every result to local disk and level unlocking is driven from that copy plus the
    /// campaignLevels catalogue. What lives here is a mirror, folded back in by
    /// <c>UpdateLocalProgressWithServerValuesIfBetter</c>, which only ever *improves* a local
    /// value. An empty mirror is therefore always safe, and a stale one can only fail to help - it
    /// can never roll a player's campaign backwards.
    ///
    /// The endpoint still has to exist and has to answer 2xx, because the read side has no backoff
    /// whatsoever:
    ///
    ///     private void OnPlayerProgressHttpRequestFailed(PlayerId playerId)
    ///     {
    ///         Debug.LogError($"Failed to get player progress ... trying again");
    ///         GetPlayerCampaignProgressInfoFromServer(playerId);   // :296
    ///     }
    ///
    /// Any non-2xx there is an unthrottled request loop bounded only by round-trip time. In-process
    /// that is far faster than it ever was over a socket, which makes answering properly more
    /// important here than it was against the HTTP backend, not less.
    /// </summary>
    public sealed class CampaignMirror
    {
        /// <summary>Ordered weakest to strongest, matching <c>CampaignService.CampaignLevelGrade</c>.
        /// The wire form of the top grade is "A++", not the enum's "A_PLUS_PLUS".</summary>
        private static readonly string[] Grades = { "FAIL", "PASS", "A", "A++" };

        private readonly Dictionary<string, CampaignScore> _scores =
            new Dictionary<string, CampaignScore>(StringComparer.Ordinal);

        private static int GradeRank(string grade)
        {
            for (var i = 0; i < Grades.Length; i++)
            {
                if (string.Equals(Grades[i], grade, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        public static string NormaliseGrade(string grade)
        {
            var text = (grade ?? string.Empty).Trim();
            if (string.Equals(text, "A_PLUS_PLUS", StringComparison.OrdinalIgnoreCase)) return "A++";

            for (var i = 0; i < Grades.Length; i++)
            {
                if (string.Equals(Grades[i], text, StringComparison.OrdinalIgnoreCase)) return Grades[i];
            }
            return "FAIL";
        }

        /// <summary>
        /// Merges one completion using the same best-of rule the client applies locally: the
        /// highest grade wins, and the lowest time and blood loss win.
        ///
        /// <paramref name="information"/> is a JSON document the client sends as a string inside
        /// the JSON body and reads back the same way - <c>{"bestTime":x,"bestBloodLoss":y}</c>.
        /// </summary>
        public void Record(string campaignId, string campaignLevelSequenceId, string grade,
                           string information, long nowMs)
        {
            var key = campaignId + " " + campaignLevelSequenceId;
            var normalised = NormaliseGrade(grade);

            double incomingTime = double.PositiveInfinity;
            double incomingBlood = double.PositiveInfinity;

            var parsed = string.IsNullOrEmpty(information) ? null : Json.TryParse(information);
            if (parsed != null)
            {
                if (parsed["bestTime"] != null) incomingTime = parsed["bestTime"].AsDouble(double.PositiveInfinity);
                if (parsed["bestBloodLoss"] != null) incomingBlood = parsed["bestBloodLoss"].AsDouble(double.PositiveInfinity);
            }

            // Carried as-is, infinities included. A first completion whose body omitted one of
            // these must leave that field unknown rather than pinning it at zero - see
            // CampaignScore.BestTime.
            if (!_scores.TryGetValue(key, out var existing))
            {
                _scores[key] = new CampaignScore
                {
                    CampaignId = campaignId,
                    CampaignLevelSequenceId = campaignLevelSequenceId,
                    Grade = normalised,
                    BestTime = incomingTime,
                    BestBloodLoss = incomingBlood,
                    CreatedAt = nowMs,
                };
                return;
            }

            if (GradeRank(normalised) > GradeRank(existing.Grade))
            {
                existing.Grade = normalised;
                existing.CreatedAt = nowMs;
            }

            if (incomingTime < existing.BestTime) existing.BestTime = incomingTime;
            if (incomingBlood < existing.BestBloodLoss) existing.BestBloodLoss = incomingBlood;
        }

        /// <summary>True for a figure the client actually reported, as opposed to one this mirror
        /// has never been told and must not invent.</summary>
        private static bool IsKnown(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Adds <paramref name="key"/> only when the value is one we were really given.
        /// An absent key is the honest way to say "no record"; any number would be a claim.</summary>
        private static void AddIfKnown(Json target, string key, double value)
        {
            if (IsKnown(value)) target.Add(key, value);
        }

        /// <summary>
        /// Shapes the mirror for <c>GetPlayerCampaignInfo</c>.
        ///
        /// The playerId must round-trip exactly: the client compares it against the one it asked
        /// for and discards the whole response on a mismatch, which looks identical to the endpoint
        /// being broken.
        ///
        /// <c>unlockedLevels</c> is always empty. The client never uploads unlock state - it
        /// derives unlocks locally from the campaignLevels catalogue - so there is nothing
        /// authentic to put there, and inventing sequence ids would risk contradicting the save.
        /// </summary>
        public Json ToResponse(string playerId)
        {
            var scores = Json.Array();
            foreach (var score in _scores.Values)
            {
                var information = Json.Object();
                AddIfKnown(information, "bestTime", score.BestTime);
                AddIfKnown(information, "bestBloodLoss", score.BestBloodLoss);

                scores.Add(Json.Object()
                    .Add("campaignId", score.CampaignId)
                    .Add("campaignLevelSequenceId", score.CampaignLevelSequenceId)
                    .Add("grade", score.Grade)
                    .Add("createdAt", score.CreatedAt)
                    .Add("information", information.ToString()));
            }

            return Json.Object()
                .Add("playerId", playerId)
                .Add("scores", scores)
                .Add("unlockedLevels", Json.Array());
        }

        public Json ToStorage()
        {
            var scores = Json.Array();
            foreach (var score in _scores.Values)
            {
                var stored = Json.Object()
                    .Add("campaignId", score.CampaignId)
                    .Add("campaignLevelSequenceId", score.CampaignLevelSequenceId)
                    .Add("grade", score.Grade)
                    .Add("createdAt", score.CreatedAt);

                // Omitted rather than flattened, so a value we never learned is still unknown
                // after a restart instead of coming back as a zero that wins every comparison.
                AddIfKnown(stored, "bestTime", score.BestTime);
                AddIfKnown(stored, "bestBloodLoss", score.BestBloodLoss);

                scores.Add(stored);
            }

            return Json.Object().Add("scores", scores);
        }

        public static CampaignMirror FromStorage(Json value)
        {
            var mirror = new CampaignMirror();
            var scores = value?["scores"];
            if (scores == null) return mirror;

            foreach (var entry in scores.Items)
            {
                var score = new CampaignScore
                {
                    CampaignId = entry["campaignId"].AsStringOr(string.Empty),
                    CampaignLevelSequenceId = entry["campaignLevelSequenceId"].AsStringOr(string.Empty),
                    Grade = NormaliseGrade(entry["grade"].AsStringOr("FAIL")),

                    // Absent means unknown, which is what an older save file that never carried
                    // these keys - or a newer one that deliberately omits them - has to read as.
                    BestTime = entry["bestTime"].AsDoubleOr(double.PositiveInfinity),
                    BestBloodLoss = entry["bestBloodLoss"].AsDoubleOr(double.PositiveInfinity),
                    CreatedAt = entry["createdAt"].AsLongOr(0),
                };
                mirror._scores[score.CampaignId + " " + score.CampaignLevelSequenceId] = score;
            }

            return mirror;
        }
    }
}
