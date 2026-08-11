using System;

namespace SS2ReviveData
{
    /// <summary>
    /// The 22-character share code, which the game already speaks at both ends.
    ///
    /// This is not something the mod invented. <c>UGCService2</c> ships
    /// <c>ConvertGUIDStringToURLFriendlyBase64</c> and its inverse, <c>ShareScreenWindow</c> shows the
    /// result and copies it to the system clipboard, and the terminal's search box feeds anything
    /// exactly 22 characters long through the inverse before searching - so typing a code into
    /// Discover has always been a level-id lookup rather than a title match:
    /// <code>
    ///   if (_ugcService.ConvertRLFriendlyBase64ToGUIDString(searchTerm, out var serverID)) {
    ///       list = new List&lt;string&gt; { serverID };
    ///       searchTerm2 = "";
    ///   }
    /// </code>
    /// That path runs through <c>SearchUGCDatabase</c>, which this mod answers from the local
    /// library. So a code posted in a chat resolves on any machine that has the level - which is
    /// what makes a file plus a code a complete sharing story with no server in it.
    ///
    /// The encoding is reproduced here rather than called through the game so that the data
    /// assembly stays free of Unity, and because the original logs the code at error level on every
    /// successful conversion. It has to stay byte-for-byte identical to the game's: the trailing
    /// <c>==</c> of the base64 is dropped, and the two URL-unsafe characters are swapped.
    /// </summary>
    public static class LevelCode
    {
        /// <summary>16 GUID bytes are 24 base64 characters, the last two of which are padding.</summary>
        public const int Length = 22;

        /// <summary>The code for a level id, or empty if the id is not a GUID.</summary>
        public static string FromLevelId(string levelId)
        {
            if (string.IsNullOrEmpty(levelId)) return string.Empty;

            try
            {
                return Convert.ToBase64String(new Guid(levelId).ToByteArray())
                              .Substring(0, Length)
                              .Replace("/", "_")
                              .Replace("+", "-");
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>The level id a code names, in the same canonical form the store keys on.</summary>
        public static bool TryToLevelId(string code, out string levelId)
        {
            levelId = string.Empty;
            if (code == null || code.Length != Length) return false;

            try
            {
                var base64 = code.Replace("_", "/").Replace("-", "+") + "==";
                levelId = new Guid(Convert.FromBase64String(base64)).ToString();
                return true;
            }
            catch (Exception)
            {
                levelId = string.Empty;
                return false;
            }
        }
    }
}
