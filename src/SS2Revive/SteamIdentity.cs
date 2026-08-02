using System;
using Data;
using Steamworks;

namespace SS2Revive
{
    /// <summary>
    /// Ownership and identity come from Steam. Bossa's account service is gone, and we do not
    /// replace it - if Steam says you own and are signed in to the app, that is the whole check.
    /// </summary>
    internal static class SteamIdentity
    {
        /// <summary>PlayerId is a fixed 36-byte buffer; every id we mint must be exactly that wide.</summary>
        private const int PlayerIdWidth = 36;

        private const string Prefix = "STEAM-";

        internal static bool IsSteamReady()
        {
            try
            {
                return SteamAPI.IsSteamRunning() && SteamUser.BLoggedOn();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Steam readiness check failed: " + ex.Message);
                return false;
            }
        }

        internal static ulong GetSteamId64()
        {
            try
            {
                return SteamUser.GetSteamID().m_SteamID;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("GetSteamID failed: " + ex.Message);
                return 0UL;
            }
        }

        internal static string GetPersonaName()
        {
            try
            {
                var name = SteamFriends.GetPersonaName();
                return string.IsNullOrEmpty(name) ? "Surgeon" : name;
            }
            catch
            {
                return "Surgeon";
            }
        }

        /// <summary>
        /// The display name of any account, not just ours. Returns empty when Steam has not cached
        /// the account yet - it is asked for, and the caller is expected to try again rather than
        /// keep a placeholder. Names are the one thing the party UI shows about other people, so a
        /// wrong-but-plausible answer here is worse than no answer.
        /// </summary>
        internal static string GetPersonaName(ulong steamId64)
        {
            if (steamId64 == 0UL)
                return string.Empty;

            try
            {
                if (steamId64 == GetSteamId64())
                    return GetPersonaName();

                var id = new CSteamID(steamId64);
                var name = SteamFriends.GetFriendPersonaName(id);

                if (string.IsNullOrEmpty(name) || name == "[unknown]")
                {
                    SteamFriends.RequestUserInformation(id, bRequireNameOnly: true);
                    return string.Empty;
                }

                return name;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Persona lookup for " + steamId64 + " failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Deterministic, stable, and unique per Steam account. The game treats PlayerId as an
        /// opaque 36-char token, so a padded SteamID64 substitutes cleanly for Bossa's GUID.
        /// </summary>
        internal static string BuildPlayerIdString(ulong steamId64)
        {
            var body = Prefix + steamId64.ToString();
            if (body.Length > PlayerIdWidth)
                body = body.Substring(0, PlayerIdWidth);
            return body.PadRight(PlayerIdWidth, '-');
        }

        internal static PlayerId BuildPlayerId(ulong steamId64)
        {
            return new PlayerId(BuildPlayerIdString(steamId64));
        }

        /// <summary>
        /// Recovers the Steam account from a player id we minted. Because the id carries the
        /// account rather than pointing at a record on a server, any lookup that used to need a
        /// round trip - "which Steam user is this player?" - is now local and cannot fail.
        /// Returns 0 for ids that did not come from us.
        /// </summary>
        internal static ulong TryGetSteamId(PlayerId playerId)
        {
            var text = playerId.ToString();
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
                return 0UL;

            var digits = text.Substring(Prefix.Length).TrimEnd('-');
            return ulong.TryParse(digits, out var steamId) ? steamId : 0UL;
        }

        /// <summary>Falls back to a fixed local id so the game still boots with Steam offline.</summary>
        internal static PlayerId GetLocalPlayerId()
        {
            var id = GetSteamId64();
            if (id != 0UL)
                return BuildPlayerId(id);

            Plugin.Log.LogWarning("No Steam id available; using offline placeholder identity.");
            return new PlayerId("STEAM-OFFLINE-LOCAL".PadRight(PlayerIdWidth, '-'));
        }
    }
}
