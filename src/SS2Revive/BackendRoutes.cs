using System;
using System.Collections.Generic;

namespace SS2Revive
{
    /// <summary>
    /// Decides, before any socket is opened, what should happen to one of Bossa's HTTP requests.
    ///
    /// Most of the endpoints the game calls have no replacement and never will - friends, UGC,
    /// moderation, Twitch integration, rich presence. Handing those to the backend only to be told
    /// 404 costs a thread hop and a log line each, and the game re-issues several of them on every
    /// menu transition. Anything not on this table is refused here instead, without the backend
    /// ever seeing it.
    ///
    /// Two endpoints get a canned local answer rather than a failure, because failing them is not
    /// free. Their failure callbacks re-request immediately with no backoff, so a 404 becomes a
    /// request loop running at whatever rate the transport allows - see the comment on
    /// <see cref="CampaignProgressBody"/>.
    /// </summary>
    internal static class BackendRoutes
    {
        internal enum Disposition
        {
            /// <summary>Hand it to the backend, which will answer it properly.</summary>
            Forward,

            /// <summary>Answer it here with a canned 200. Never touches the network.</summary>
            AnswerLocally,

            /// <summary>Answer it here with the dead-endpoint status. Never touches the network.</summary>
            Block,
        }

        /// <summary>
        /// An empty but structurally valid campaign mirror, used only when the backend is
        /// disabled or failed to start.
        ///
        /// CampaignPlayerProgressLoader.OnPlayerProgressHttpRequestFailed re-issues this request
        /// the instant it fails, with no delay and no attempt limit, so a 404 here is a hot loop
        /// rather than a one-off error. An empty mirror is safe: campaign progress lives in the
        /// local save, and the server copy is only ever merged in where it beats the local value.
        ///
        /// {0} is the player ID from the URL. It has to match what the client asked for, or the
        /// response is discarded with "PlayerId in response does not match requested PlayerId".
        /// </summary>
        private const string CampaignProgressBody =
            "{{\"playerId\":\"{0}\",\"scores\":[],\"unlockedLevels\":[]}}";

        /// <summary>
        /// UGC is gone, so nobody has favourite levels. LocalUGCLevelCache caches a successful
        /// answer for the rest of the session and re-asks after every failure, so answering once
        /// is cheaper than refusing repeatedly.
        /// </summary>
        private const string EmptyArrayBody = "[]";

        private sealed class Route
        {
            internal string Verb;
            internal string[] Pattern;
            internal bool Forward;
            internal string LocalBody;
        }

        /// <summary>
        /// A "*" matches exactly one path segment. Order does not matter; patterns are distinct.
        /// </summary>
        private static readonly Route[] Routes =
        {
            Forwarded("GET", "/version"),
            Forwarded("GET", "/status"),
            Forwarded("GET", "/scheduledTimes"),

            Forwarded("POST", "/auth/authenticate"),
            Forwarded("POST", "/auth/refreshJWT"),

            Forwarded("GET", "/profile/players/*"),

            Forwarded("GET", "/player-progression/playerProgression/players/*/progression"),
            Forwarded("POST", "/player-progression/campaignLevels/*/players/*/completed"),

            // Answered by the backend when it is up, canned when it is not. Never simply failed.
            Forwarded("GET", "/player-progression/players/*/campaigns", CampaignProgressBody),

            Local("GET", "/player-progression/players/*/favouriteLevels", EmptyArrayBody),

            // Cosmetics. The backend answers these once it has the item catalogue, which in Local
            // mode it reads out of the game's own StreamingAssets at startup and so always has.
            // Without one it answers 404 and the client keeps the unlocks it predicted locally -
            // the safe failure, because a partial list is read as a deletion.
            Forwarded("GET", "/player-progression/itemSets"),
            Forwarded("GET", "/player-progression/playerProgression/players/*/inventory"),
            Forwarded("DELETE", "/player-progression/playerProgression/players/*/inventory"),
            Forwarded("POST", "/player-progression/playerProgression/players/*/addCharacter"),
            Forwarded("POST", "/player-progression/playerProgression/players/*/characters/*/setEquippedItems"),

            Forwarded("POST", "/player-progression/quickplay/completeLevel"),

            Forwarded("GET", "/challenges/challenges/players/*/currentDailyChallenges"),
            Forwarded("POST", "/challenges/challenges/players/*/challenges/*"),
            Forwarded("POST", "/challenges/challenges/forceResetPlayerDailyChallenges"),
            Forwarded("POST", "/challenges/challenges/expiringPlayerDailyChallenges"),
        };

        private static Route Forwarded(string verb, string pattern, string localBody = null)
        {
            return new Route
            {
                Verb = verb,
                Pattern = Split(pattern),
                Forward = true,
                LocalBody = localBody,
            };
        }

        private static Route Local(string verb, string pattern, string localBody)
        {
            return new Route
            {
                Verb = verb,
                Pattern = Split(pattern),
                Forward = false,
                LocalBody = localBody,
            };
        }

        private static string[] Split(string path)
        {
            return path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static readonly HashSet<string> ReportedBlocks = new HashSet<string>();

        /// <summary>
        /// Resolves one request. <paramref name="backendAvailable"/> is passed in rather than read
        /// from <see cref="BackendClient"/> so this stays independently testable and so the
        /// decision is made once per request instead of racing a field.
        /// </summary>
        internal static Disposition Resolve(string verb, string path, bool backendAvailable,
                                            out string localBody)
        {
            localBody = null;

            var query = path.IndexOf('?');
            var withoutQuery = query >= 0 ? path.Substring(0, query) : path;
            var segments = Split(withoutQuery);

            var route = Match(verb, segments);
            if (route == null)
            {
                ReportBlockOnce(verb, segments);
                return Disposition.Block;
            }

            if (route.Forward && backendAvailable)
                return Disposition.Forward;

            if (route.LocalBody == null)
                return Disposition.Block;

            localBody = route.LocalBody.IndexOf('{') >= 0
                ? string.Format(route.LocalBody, FirstWildcard(route, segments))
                : route.LocalBody;

            return Disposition.AnswerLocally;
        }

        private static Route Match(string verb, string[] segments)
        {
            for (var i = 0; i < Routes.Length; i++)
            {
                var route = Routes[i];

                if (!string.Equals(route.Verb, verb, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (route.Pattern.Length != segments.Length)
                    continue;

                var matched = true;
                for (var s = 0; s < segments.Length; s++)
                {
                    if (route.Pattern[s] == "*")
                        continue;
                    if (string.Equals(route.Pattern[s], segments[s], StringComparison.OrdinalIgnoreCase))
                        continue;

                    matched = false;
                    break;
                }

                if (matched)
                    return route;
            }

            return null;
        }

        private static string FirstWildcard(Route route, string[] segments)
        {
            for (var i = 0; i < route.Pattern.Length && i < segments.Length; i++)
            {
                if (route.Pattern[i] == "*")
                    return segments[i];
            }

            return string.Empty;
        }

        /// <summary>
        /// Once per distinct endpoint per session. These are expected and mostly harmless, but
        /// seeing the list once is what tells us whether something worth implementing is being
        /// silently refused.
        ///
        /// Keyed on the endpoint shape, not the literal path: segments carrying an ID are folded
        /// to "*" so browsing a hundred UGC levels produces one line rather than a hundred.
        /// </summary>
        private static void ReportBlockOnce(string verb, string[] segments)
        {
            var shape = new System.Text.StringBuilder(verb);
            for (var i = 0; i < segments.Length; i++)
            {
                shape.Append('/');
                shape.Append(LooksLikeIdentifier(segments[i]) ? "*" : segments[i]);
            }

            var key = shape.ToString();

            lock (ReportedBlocks)
            {
                if (!ReportedBlocks.Add(key))
                    return;
            }

            Plugin.Log.LogInfo("Blocked locally, no backend route: " + key);
        }

        /// <summary>
        /// Player IDs, GUIDs and numeric IDs, as opposed to fixed route names.
        ///
        /// The thresholds matter for readability: player IDs are 36 characters and GUIDs 32 or 36,
        /// while the longest real route names are "player-progression" and "playerProgression" at
        /// 18 and 17. Folding on mere length, or on containing any digit, would collapse those and
        /// "SURGEON2" into "*" and make the logged shape useless for identifying the endpoint.
        /// </summary>
        private static bool LooksLikeIdentifier(string segment)
        {
            if (segment.Length >= 24)
                return true;

            if (segment.Length == 0)
                return false;

            for (var i = 0; i < segment.Length; i++)
            {
                if (segment[i] < '0' || segment[i] > '9')
                    return false;
            }

            return true;
        }
    }
}
