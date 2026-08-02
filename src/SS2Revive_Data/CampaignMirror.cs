using System;
using System.Collections.Generic;

namespace SS2ReviveData
{
    public sealed class CampaignScore
    {
        public string CampaignId;
        public string CampaignLevelSequenceId;
        public string Grade;
        public double BestTime;
        public double BestBloodLoss;
        public long CreatedAt;
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

            if (!_scores.TryGetValue(key, out var existing))
            {
                _scores[key] = new CampaignScore
                {
                    CampaignId = campaignId,
                    CampaignLevelSequenceId = campaignLevelSequenceId,
                    Grade = normalised,
                    BestTime = Finite(incomingTime, 0),
                    BestBloodLoss = Finite(incomingBlood, 0),
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

        private static double Finite(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

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
                var information = Json.Object()
                    .Add("bestTime", Finite(score.BestTime, 0))
                    .Add("bestBloodLoss", Finite(score.BestBloodLoss, 0));

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
                scores.Add(Json.Object()
                    .Add("campaignId", score.CampaignId)
                    .Add("campaignLevelSequenceId", score.CampaignLevelSequenceId)
                    .Add("grade", score.Grade)
                    .Add("bestTime", Finite(score.BestTime, 0))
                    .Add("bestBloodLoss", Finite(score.BestBloodLoss, 0))
                    .Add("createdAt", score.CreatedAt));
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
                    BestTime = entry["bestTime"].AsDoubleOr(0),
                    BestBloodLoss = entry["bestBloodLoss"].AsDoubleOr(0),
                    CreatedAt = entry["createdAt"].AsLongOr(0),
                };
                mirror._scores[score.CampaignId + " " + score.CampaignLevelSequenceId] = score;
            }

            return mirror;
        }
    }
}
