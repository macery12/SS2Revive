using System;
using System.Collections.Generic;
using System.IO;
using SS2ReviveData;

namespace SS2Revive
{
    /// <summary>
    /// Levels moving between machines as files, because there is no service left to move them
    /// through.
    ///
    /// The shape of this is set by what the game already does rather than by what would be tidy.
    /// Surgeon Simulator 2 ships a share window that shows a 22-character code and copies it to the
    /// clipboard, and a search box that turns a code of exactly that length back into a level-id
    /// lookup. Both still work; the only thing missing was ever a way for the bytes to arrive. So a
    /// level is exported to one file, that file is sent however people already talk to each other,
    /// and the code stays what it always was - the name of the level, not the level itself.
    ///
    /// The code cannot carry the level, and it is worth being plain about why. A level blob is
    /// kilobytes at the small end and megabytes at the large; a chat message is two thousand
    /// characters. Even a nearly empty level does not fit, so any design where the code is the
    /// whole payload was never available.
    ///
    /// What arrives is somebody else's level and stays that way: it comes in published, credited to
    /// whoever built it, under the id it was exported with. That last part is what makes the code
    /// worth anything - the same level answers to the same code on every machine that has it, which
    /// is the property a shared index would otherwise have had to provide.
    /// </summary>
    internal static class LevelSharing
    {
        private const int MaxFilesPerPass = 8;
        internal static string ExportDirectory { get; private set; }
        internal static string ImportDirectory { get; private set; }

        /// <summary>
        /// Where a bundle goes once it has been taken in. Moving it is what stops the folder being
        /// imported twice - the Create screen scans it every time it opens. A repeated current
        /// revision is harmless, but moving handled files keeps the inbox and its feedback clear.
        /// </summary>
        internal static string ImportedDirectory { get; private set; }
        internal static string RejectedDirectory { get; private set; }

        /// <summary>
        /// Files taken in this session, as a backstop for the move above failing - a bundle still
        /// open in the browser that downloaded it, say. Without it a failed move would import the
        /// same file again on the next scan.
        /// </summary>
        private static readonly HashSet<string> Handled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool Available => ExportDirectory != null && UgcBackend.Store != null;

        internal static void Initialise(string saveDirectory)
        {
            try
            {
                var root = SaveLocation.ResolveDirectory(saveDirectory);

                ExportDirectory = Path.Combine(root, "export");
                ImportDirectory = Path.Combine(root, "import");
                ImportedDirectory = Path.Combine(ImportDirectory, "imported");
                RejectedDirectory = Path.Combine(ImportDirectory, "rejected");

                // Created now rather than on first use, so both folders are there to be found by
                // somebody who has been told to put a file in one of them.
                Directory.CreateDirectory(ExportDirectory);
                Directory.CreateDirectory(ImportDirectory);
                Directory.CreateDirectory(ImportedDirectory);
                Directory.CreateDirectory(RejectedDirectory);

                Plugin.Log.LogInfo("Level sharing ready. Export: " + ExportDirectory
                                   + " | Import: " + ImportDirectory);
            }
            catch (Exception ex)
            {
                ExportDirectory = null;
                ImportDirectory = null;
                Plugin.Log.LogError("Could not prepare the level sharing folders; export and import "
                                    + "are off for this session. " + ex);
            }
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// Writes one level to <c>export\</c> and answers with the share code for it.
        ///
        /// Overwriting a file of the same name is deliberate. The name carries the level's id, so
        /// the only file it can collide with is an older export of the same level, and keeping that
        /// one would leave the player with two files and no way to tell which is current.
        /// </summary>
        internal static bool Export(string serverLevelId, out string code, out string message)
        {
            code = string.Empty;
            message = null;

            var store = UgcBackend.Store;
            if (store == null || ExportDirectory == null)
            {
                message = "The level library is not open, so nothing can be exported.";
                return false;
            }

            var level = store.Get(serverLevelId);
            if (level == null)
            {
                message = "That level is not in this machine's library.";
                return false;
            }

            string error;
            var bundle = LevelBundle.FromLevel(level, store.ReadKey, out error);
            if (bundle == null)
            {
                message = error ?? "The level could not be read.";
                return false;
            }

            bundle.ReviveVersion = Plugin.PluginVersion;

            var fileName = bundle.SuggestedFileName();
            var path = Path.Combine(ExportDirectory, fileName);

            try
            {
                AtomicFile.WriteAllBytes(path, bundle.Pack());
                RemoveOlderExports(bundle.Code, path);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Exporting '" + level.Title + "' failed: " + ex);
                message = "The file could not be written: " + ex.Message;
                return false;
            }

            code = bundle.Code;

            Plugin.Log.LogInfo("Exported '" + level.Title + "' (" + level.Id + ") to " + path
                               + ". Share code " + code + ".");
            return true;
        }

        private static void RemoveOlderExports(string code, string keepPath)
        {
            if (string.IsNullOrEmpty(code) || ExportDirectory == null) return;

            try
            {
                var files = Directory.GetFiles(ExportDirectory, "*" + LevelBundle.Extension,
                                               SearchOption.TopDirectoryOnly);
                var marker = "[" + code + "]";
                for (var i = 0; i < files.Length; i++)
                {
                    if (string.Equals(files[i], keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(files[i]).IndexOf(marker,
                            StringComparison.OrdinalIgnoreCase) < 0) continue;
                    File.Delete(files[i]);
                }
            }
            catch (Exception ex)
            {
                // The new export is already durable. A stale older filename is untidy, not a
                // reason to report that the export itself failed.
                Plugin.Log.LogWarning("Could not remove an older export for " + code + ": "
                                      + ex.Message);
            }
        }

        // ------------------------------------------------------------------ import

        /// <summary>What one pass over the import folder did.</summary>
        internal struct ImportResult
        {
            internal int Imported;
            internal int Updated;
            internal int Current;
            internal int Older;
            internal int Rejected;
            internal int Pending;
            internal string FirstError;

            internal int Seen => Imported + Updated + Current + Older + Rejected;
            internal int Added => Imported + Updated;
        }

        /// <summary>
        /// Reads every bundle sitting in <c>import\</c> into the library, and moves each one into
        /// <c>import\imported\</c> as it goes.
        ///
        /// The install itself is idempotent by id and revision; the move also keeps an already
        /// handled file from producing the same "current" message on every screen visit.
        /// </summary>
        internal static ImportResult ImportAll()
        {
            var result = new ImportResult();

            var store = UgcBackend.Store;
            if (store == null || ImportDirectory == null) return result;

            string[] files;
            try
            {
                files = Directory.GetFiles(ImportDirectory, "*" + LevelBundle.Extension,
                                           SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read the import folder: " + ex.Message);
                return result;
            }

            var attempted = 0;
            for (var i = 0; i < files.Length; i++)
            {
                if (Handled.Contains(files[i])) continue;
                if (attempted >= MaxFilesPerPass)
                {
                    result.Pending++;
                    continue;
                }
                attempted++;
                ImportOne(store, files[i], ref result);
            }

            return result;
        }

        private static void ImportOne(UgcStore store, string path, ref ImportResult result)
        {
            var name = Path.GetFileName(path);

            byte[] bytes;
            try
            {
                var info = new FileInfo(path);
                // A browser or Explorer copy exposes its destination before the last byte has
                // arrived. Leave recently-written files alone so the next deliberate scan sees a
                // complete bundle instead of quarantining a transient truncation.
                if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(2)) return;

                if (info.Length <= 0 || info.Length > LevelBundle.MaxBundleBytes)
                {
                    Reject(ref result, path, "its size is outside the 1-"
                                             + (LevelBundle.MaxBundleBytes / (1024 * 1024)) + " MB limit");
                    return;
                }

                // Open once and keep the handle while checking and reading. This prevents a file
                // from being replaced or grown between FileInfo.Length and ReadAllBytes, which
                // would otherwise allocate before the bundle reader gets a chance to reject it.
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length <= 0 || input.Length > LevelBundle.MaxBundleBytes)
                    {
                        Reject(ref result, path, "its size is outside the 1-"
                                                 + (LevelBundle.MaxBundleBytes / (1024 * 1024)) + " MB limit");
                        return;
                    }

                    bytes = new byte[(int)input.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = input.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) throw new EndOfStreamException("The bundle changed while it was read.");
                        offset += read;
                    }
                    if (input.ReadByte() != -1)
                        throw new InvalidDataException("The bundle grew while it was read.");
                }
            }
            catch (Exception ex)
            {
                // Sharing clients and browsers can hold the destination open briefly after its
                // final write. Treat I/O failures as transient and retry on the next scan.
                Plugin.Log.LogWarning("Could not read " + name + " yet: " + ex.Message);
                return;
            }

            string error;
            var bundle = LevelBundle.Unpack(bytes, out error);
            if (bundle == null)
            {
                Reject(ref result, path, error);
                return;
            }

            var prototype = new UgcLevelRecord
            {
                Id = bundle.Id,
                Title = bundle.Title,
                Description = bundle.Description,
                CreatorIds = new List<string>(bundle.CreatorIds),
                Tags = new List<string>(bundle.Tags),
                CreatedAtMs = bundle.CreatedAtMs,
                Configurations = bundle.Configurations,
                Validations = bundle.Validations,
            };

            UgcInstallOutcome outcome;
            var adopted = store.Install(prototype, bundle.ClientVersion, bundle.ContentVersion,
                                        bundle.ExportedAtMs, bundle.Content, bundle.ContentImage,
                                        bundle.Thumbnail, out outcome, out error);

            if (outcome == UgcInstallOutcome.Failed || outcome == UgcInstallOutcome.Conflict)
            {
                Reject(ref result, path, error);
                return;
            }

            switch (outcome)
            {
                case UgcInstallOutcome.Added: result.Imported++; break;
                case UgcInstallOutcome.Updated: result.Updated++; break;
                case UgcInstallOutcome.Current: result.Current++; break;
                case UgcInstallOutcome.Older: result.Older++; break;
            }

            Plugin.Log.LogInfo((outcome == UgcInstallOutcome.Updated ? "Updated '"
                               : outcome == UgcInstallOutcome.Current ? "Already current: '"
                               : outcome == UgcInstallOutcome.Older ? "Ignored older copy of '"
                               : "Installed '") + adopted.Title + "' (" + adopted.Id + ", revision "
                               + adopted.LatestContent().ContentVersion + ", code "
                               + LevelCode.FromLevelId(adopted.Id) + ") from " + name + ".");

            MoveAside(path);
        }

        /// <summary>
        /// Moves a bundle out of the way once it is in the library, so the next scan does not see
        /// it again. A failure here is recorded in memory instead, which covers the session; the
        /// worst a restart can then do is add one more copy, which is visible and deletable rather
        /// than silent.
        /// </summary>
        private static void MoveAside(string path)
        {
            Handled.Add(path);

            if (ImportedDirectory == null) return;

            try
            {
                Directory.CreateDirectory(ImportedDirectory);

                var name = Path.GetFileNameWithoutExtension(path);
                var extension = Path.GetExtension(path);

                var destination = Path.Combine(ImportedDirectory, name + extension);
                for (var n = 2; File.Exists(destination) && n < 1000; n++)
                    destination = Path.Combine(ImportedDirectory, name + " (" + n + ")" + extension);

                File.Move(path, destination);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Imported " + Path.GetFileName(path) + " but could not move it "
                                      + "into the imported folder, so please move or delete it "
                                      + "yourself: " + ex.Message);
            }
        }

        private static void Reject(ref ImportResult result, string path, string reason)
        {
            result.Rejected++;
            if (result.FirstError == null) result.FirstError = reason;

            Plugin.Log.LogWarning("Skipped " + Path.GetFileName(path) + ": "
                                  + (reason ?? "it could not be read."));

            MoveRejected(path);
        }

        private static void MoveRejected(string path)
        {
            Handled.Add(path);
            if (RejectedDirectory == null) return;

            try
            {
                Directory.CreateDirectory(RejectedDirectory);
                var name = Path.GetFileNameWithoutExtension(path);
                var extension = Path.GetExtension(path);
                var destination = Path.Combine(RejectedDirectory, name + extension);
                for (var n = 2; File.Exists(destination) && n < 1000; n++)
                    destination = Path.Combine(RejectedDirectory, name + " (" + n + ")" + extension);
                File.Move(path, destination);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not move rejected " + Path.GetFileName(path)
                                      + " into quarantine: " + ex.Message);
            }
        }

        /// <summary>
        /// One line fit for an in-game prompt. Null when there is nothing worth interrupting the
        /// player about, which is the usual case on a screen that scans the folder every time it
        /// opens.
        /// </summary>
        internal static string Describe(ImportResult result, bool sayNothingHappened)
        {
            if (result.Added > 0)
            {
                var line = result.Imported > 0 && result.Updated > 0
                    ? "Installed " + result.Imported + " and updated " + result.Updated + " levels"
                    : result.Updated > 0
                        ? (result.Updated == 1 ? "Updated 1 level" : "Updated " + result.Updated + " levels")
                        : (result.Imported == 1 ? "Installed 1 level" : "Installed " + result.Imported + " levels");

                line += ". Look under Discover.";

                if (result.Current > 0) line += " " + result.Current + " already current.";
                if (result.Older > 0) line += " " + result.Older + " older version skipped.";
                if (result.Pending > 0) line += " More files are waiting; press Import again.";

                return result.Rejected > 0 ? line + " " + result.Rejected + " skipped." : line;
            }

            if (result.Current > 0 || result.Older > 0)
            {
                var line = result.Current > 0
                    ? (result.Current == 1 ? "That level is already current."
                                          : result.Current + " levels are already current.")
                    : string.Empty;
                if (result.Older > 0)
                    line += (line.Length > 0 ? " " : string.Empty) + result.Older
                            + (result.Older == 1 ? " older version was skipped."
                                                : " older versions were skipped.");
                return line;
            }

            if (result.Rejected > 0)
            {
                return result.Rejected == 1
                    ? "Could not import that file: " + (result.FirstError ?? "it could not be read.")
                    : "Could not import " + result.Rejected + " files. See the log for why.";
            }

            return sayNothingHappened
                ? (result.Pending > 0
                    ? "More files are waiting; press Import again."
                    : "Nothing to import. Put .ss2level files in the import folder and press this again.")
                : null;
        }
    }
}
