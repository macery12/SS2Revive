using System;
using System.IO;
using System.Text;

namespace SS2ReviveData
{
    /// <summary>
    /// Writes a file in a way that a crash cannot leave half-written.
    ///
    /// Everything this library persists is rewritten whole rather than appended to - the progress
    /// file, a level's metadata, a level's blobs - so every write is a moment where the old copy
    /// and the new one both have to exist somewhere until the swap is complete.
    ///
    /// The obvious shape, write-temp then delete-then-move, has a window between the delete and the
    /// move where neither file exists. It is small, and it is exactly the window in which losing
    /// power costs the player everything rather than the newest change. <see cref="File.Replace"/>
    /// closes it: the swap is a single filesystem operation, and it hands back the file it
    /// displaced as a backup rather than unlinking it.
    ///
    /// Replace only works within one volume and refuses some filesystems outright, so the
    /// delete-then-move path is kept as the fallback. That is no worse than what this replaced.
    /// </summary>
    internal static class AtomicFile
    {
        internal const string BackupSuffix = ".bak";

        internal static void WriteAllText(string path, string contents, Encoding encoding,
                                          bool keepBackup = false)
        {
            Write(path, keepBackup, temporary => File.WriteAllText(temporary, contents, encoding));
        }

        internal static void WriteAllBytes(string path, byte[] contents, bool keepBackup = false)
        {
            Write(path, keepBackup, temporary => File.WriteAllBytes(temporary, contents));
        }

        /// <summary>
        /// Throws on failure, and deliberately so - every caller here already decides for itself
        /// whether a failed write is worth reporting or worth taking the game down for, and they
        /// do not agree. See LocalStore.Save, which swallows.
        /// </summary>
        private static void Write(string path, bool keepBackup, Action<string> writeTemporary)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = path + ".tmp";
            writeTemporary(temporary);

            if (!File.Exists(path))
            {
                // Nothing to displace, so there is no window to close and Replace would throw.
                File.Move(temporary, path);
                return;
            }

            var backup = keepBackup ? path + BackupSuffix : null;

            try
            {
                // ignoreMetadataErrors: we are replacing our own file with our own file, and a
                // failure to carry ACLs across is not a reason to abandon the write.
                File.Replace(temporary, path, backup, true);
                return;
            }
            catch (Exception)
            {
                // Replace is unavailable across volumes and on some filesystems. Fall through.
            }

            File.Delete(path);
            File.Move(temporary, path);
        }
    }
}
