using System;
using System.Collections.Generic;
using System.Globalization;
using SS2ReviveData;
using Steamworks;

namespace SS2Revive
{
    /// <summary>
    /// Carries the one piece of information that genuinely crosses between players.
    ///
    /// Everything else Bossa's backend held turned out to be per-player: daily challenges are
    /// seeded from the player id and the UTC date, campaign progress mirrors a local save, the
    /// cosmetics catalogue ships inside the game, and the end-of-match XP screen is computed
    /// client-side. What remains is <c>GetPlayerLevelExperience(self, someone-else)</c> - the level
    /// number shown beside a party member or a friend.
    ///
    /// That does not need a server either, because Steam already carries per-user key/value data
    /// between exactly the people who need to see it:
    ///
    ///   lobby member data  - visible to everyone in the party, which is when the party screen asks
    ///   rich presence      - visible to friends, which is when the friend-online toast asks
    ///
    /// Both are published here and read back into a snapshot the backend can consult from its
    /// worker thread. Steam's API is main-thread-only, hence the snapshot rather than a direct call.
    ///
    /// The failure mode worth knowing about: Steam answers "not downloaded yet" as an empty string
    /// with no error. Nothing here ever caches an empty answer, and the backend refuses the request
    /// rather than substituting a plausible level - a friend shown as level 1 when they are level
    /// 40 is worse than a missing toast.
    /// </summary>
    internal static class SteamPeers
    {
        /// <summary>Namespaced, because this shares a key space with the game's own rich presence
        /// (which uses "connect") and with any other mod.</summary>
        private const string Key = "ss2revive_level";

        private const uint AppId = 774791;

        private const double PublishIntervalSeconds = 2.0;
        private const double FriendScanIntervalSeconds = 30.0;

        private sealed class PeerLevel
        {
            internal int SeasonLevel;
            internal long SeasonXp;
            internal string UserName;
        }

        private static readonly Dictionary<string, PeerLevel> Known =
            new Dictionary<string, PeerLevel>(StringComparer.Ordinal);

        private static readonly PeerDirectory Snapshot = new PeerDirectory();

        private static string _published;
        private static double _nextPublish;
        private static double _nextFriendScan;

        internal static IPeerDirectory Directory => Snapshot;

        /// <summary>
        /// Reads the snapshot. Separate from the collection so the lock stays private and so
        /// <c>SS2ReviveData</c> keeps no reference to Steamworks.
        /// </summary>
        private sealed class PeerDirectory : IPeerDirectory
        {
            public bool TryGetLevel(string playerId, out int seasonLevel, out long seasonXp,
                                    out string userName)
            {
                lock (Known)
                {
                    if (Known.TryGetValue(playerId, out var peer))
                    {
                        seasonLevel = peer.SeasonLevel;
                        seasonXp = peer.SeasonXp;
                        userName = peer.UserName;
                        return true;
                    }
                }

                seasonLevel = 0;
                seasonXp = 0;
                userName = null;
                return false;
            }
        }

        /// <summary>Driven from <see cref="Dispatcher"/>'s Update, which is the only main-thread
        /// tick the plugin has. Cheap enough to call every frame; it does its own throttling.</summary>
        internal static void Tick(double timeSeconds)
        {
            if (!Plugin.ShareLevel.Value) return;
            if (timeSeconds < _nextPublish) return;
            _nextPublish = timeSeconds + PublishIntervalSeconds;

            try
            {
                Publish();
                ReadLobbyMembers();

                if (timeSeconds >= _nextFriendScan)
                {
                    _nextFriendScan = timeSeconds + FriendScanIntervalSeconds;
                    ReadFriends();
                }
            }
            catch (Exception ex)
            {
                // Steam being unavailable must never interrupt the frame. Losing a level number
                // costs a toast; throwing here would cost the game.
                Plugin.Log.LogWarning("Steam peer exchange failed: " + ex.Message);
            }
        }

        private static void Publish()
        {
            if (!LocalBackendHost.Available) return;
            if (!LocalBackendHost.Backend.TryGetLocalLevel(out var level, out var xp)) return;

            var value = level.ToString(CultureInfo.InvariantCulture) + ":"
                        + xp.ToString(CultureInfo.InvariantCulture);

            // Lobby member data is republished on every lobby, because joining a new one does not
            // carry the old lobby's values across. Rich presence is global to the session, so it
            // only needs writing when the number changes.
            if (PartyBackend.InLobby)
                SteamMatchmaking.SetLobbyMemberData(PartyBackend.Lobby, Key, value);

            if (string.Equals(_published, value, StringComparison.Ordinal)) return;
            _published = value;
            SteamFriends.SetRichPresence(Key, value);
        }

        private static void ReadLobbyMembers()
        {
            if (!PartyBackend.InLobby) return;

            var lobby = PartyBackend.Lobby;
            var self = SteamUser.GetSteamID();
            var count = SteamMatchmaking.GetNumLobbyMembers(lobby);

            for (var i = 0; i < count; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (member == self) continue;

                Record(member, SteamMatchmaking.GetLobbyMemberData(lobby, member, Key));
            }
        }

        /// <summary>
        /// Friends who are in this game, so the "friend came online" notification has a level to
        /// show. Scanned rarely - the friends list can be long and none of this is time critical.
        /// </summary>
        private static void ReadFriends()
        {
            var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);

            for (var i = 0; i < count; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);

                if (!SteamFriends.GetFriendGamePlayed(friend, out var game)) continue;
                if (game.m_gameID.AppID().m_AppId != AppId) continue;

                var value = SteamFriends.GetFriendRichPresence(friend, Key);
                if (string.IsNullOrEmpty(value))
                {
                    // Ask, and pick it up on a later pass. Steam delivers this asynchronously and
                    // reports "not yet" identically to "never set".
                    SteamFriends.RequestFriendRichPresence(friend);
                    continue;
                }

                Record(friend, value);
            }
        }

        private static void Record(CSteamID steamId, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            var separator = value.IndexOf(':');
            if (separator <= 0) return;

            if (!int.TryParse(value.Substring(0, separator), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out var level))
            {
                return;
            }

            long.TryParse(value.Substring(separator + 1), NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var xp);

            if (level < 1) return;

            var playerId = PlayerIds.FromSteamId(steamId.m_SteamID);
            var userName = SteamIdentity.GetPersonaName(steamId.m_SteamID);

            lock (Known)
            {
                if (!Known.TryGetValue(playerId, out var peer))
                {
                    peer = new PeerLevel();
                    Known[playerId] = peer;
                    Plugin.Log.LogInfo("Learned season level " + level + " for " + playerId
                                       + " over Steam.");
                }

                peer.SeasonLevel = level;
                peer.SeasonXp = xp;

                // Only overwrite with a name Steam actually gave us; an empty one means the
                // account is still downloading, not that it has no name.
                if (!string.IsNullOrEmpty(userName)) peer.UserName = userName;
            }
        }
    }
}
