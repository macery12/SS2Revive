using System;

namespace SS2ReviveData
{
    /// <summary>
    /// The one identity rule everything else depends on.
    ///
    /// Bossa's <c>PlayerId</c> is a fixed 36-character buffer, so ours has to be exactly that wide
    /// or the value is truncated somewhere unhelpful. Encoding the Steam account into the id rather
    /// than allocating an opaque token is what makes a serverless build possible at all: every
    /// question of the form "which Steam user is this player?" becomes a local string operation
    /// instead of a lookup against a record only a server would hold.
    ///
    /// This is a duplicate of the rule in the plugin's SteamIdentity, kept here because this
    /// assembly has to be able to decode ids without referencing Steamworks - and because the two
    /// disagreeing would be a silent, hard-to-see failure. Both are tested against
    /// <see cref="Width"/>.
    /// </summary>
    public static class PlayerIds
    {
        public const int Width = 36;

        private const string Prefix = "STEAM-";

        public static bool IsWellFormed(string playerId)
        {
            return !string.IsNullOrEmpty(playerId) && playerId.Length == Width;
        }

        public static string FromSteamId(ulong steamId64)
        {
            var body = Prefix + steamId64.ToString();
            if (body.Length > Width) body = body.Substring(0, Width);
            return body.PadRight(Width, '-');
        }

        /// <summary>Returns 0 for ids that did not come from us.</summary>
        public static ulong ToSteamId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || !playerId.StartsWith(Prefix, StringComparison.Ordinal))
                return 0UL;

            var digits = playerId.Substring(Prefix.Length).TrimEnd('-');
            return ulong.TryParse(digits, out var steamId) ? steamId : 0UL;
        }

        /// <summary>A name to show when Steam has not given us a real one. Deliberately derived
        /// from the account so two unknown players never collapse into the same label.</summary>
        public static string PlaceholderName(string playerId)
        {
            var steamId = ToSteamId(playerId);
            if (steamId == 0UL) return "Surgeon";

            var text = steamId.ToString();
            return "Surgeon " + (text.Length <= 4 ? text : text.Substring(text.Length - 4));
        }
    }
}
