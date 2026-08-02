using System;
using System.IO;

namespace SS2ReviveData
{
    /// <summary>
    /// Where the serverless backend keeps its state.
    ///
    /// It goes next to the game's own saves, not inside the game folder and not inside BepInEx.
    /// Bossa put everything under
    /// <c>%LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\</c> - Data, Campaign, Levels, Settings
    /// and Cache are all siblings there (see <c>Data.Constants.GAME_SAVE_DIRECTORY</c>) - so a
    /// sibling <c>SS2Revive</c> folder puts progress exactly where a player would look for it and
    /// where their existing backup habits already point.
    ///
    /// The two locations this must never be are the ones that get destroyed by ordinary
    /// maintenance: a Steam "verify integrity of game files" can remove unknown files under the
    /// install directory, and reinstalling BepInEx or updating the mod means deleting the plugins
    /// folder. Progress that lives in either is progress that quietly disappears one day.
    /// </summary>
    public static class SaveLocation
    {
        private const string Publisher = "Bossa Studios";
        private const string Product = "Surgeon Simulator 2";
        private const string Folder = "SS2Revive";

        /// <summary>
        /// The directory, created if absent.
        ///
        /// LocalApplicationData is the primary because it is where the game already writes. The
        /// Documents fallback exists for the case where that folder cannot be resolved or written
        /// to at all - a redirected profile, mostly - and is the "easy to find place" answer rather
        /// than a second silent hiding spot.
        /// </summary>
        public static string ResolveDirectory(string configuredOverride = null)
        {
            if (!string.IsNullOrEmpty(configuredOverride))
            {
                var expanded = Environment.ExpandEnvironmentVariables(configuredOverride.Trim());
                if (TryCreate(expanded, out var overridden)) return overridden;
            }

            var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
            if (localAppData != null)
            {
                var preferred = Path.Combine(Path.Combine(localAppData, Publisher), Path.Combine(Product, Folder));
                if (TryCreate(preferred, out var created)) return created;
            }

            var documents = SafeFolder(Environment.SpecialFolder.MyDocuments);
            if (documents != null)
            {
                var fallback = Path.Combine(Path.Combine(documents, "My Games"), Path.Combine(Product, Folder));
                if (TryCreate(fallback, out var created)) return created;
            }

            // Last resort: the working directory. Reached only when both known-good roots are
            // unwritable, which is already a broken machine - but returning a path the caller can
            // fail on cleanly beats returning null and crashing at the first write.
            return Path.GetFullPath(Folder);
        }

        public static string StateFile(string directory) => Path.Combine(directory, "progress.json");

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryCreate(string path, out string created)
        {
            created = null;
            try
            {
                Directory.CreateDirectory(path);
                created = path;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
