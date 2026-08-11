using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SS2ReviveData;

namespace SS2Revive
{
    /// <summary>
    /// Read-only client for the public community-map catalogue. Browsing and downloading published
    /// maps intentionally require no account; authenticated upload is a separate API boundary.
    /// </summary>
    internal static class CommunityCatalogClient
    {
        internal const string KeyPrefix = "ss2revive-catalog/";

        private const int RequestTimeoutMs = 10000;
        private const long MaxCacheBytes = 128L * 1024 * 1024;
        private const int MaxCacheFiles = 128;

        private static readonly object Gate = new object();
        private static readonly object ObjectCacheGate = new object();
        private static readonly Dictionary<string, int> ConfirmedPublished =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

        private static UgcStore _store;
        private static Uri _catalogUri;
        private static Uri _objectsBaseUri;
        private static string _catalogCacheFile;
        private static string _objectCacheDirectory;
        private static CommunityCatalog _catalog;
        private static bool _refreshing;
        private static DateTime _lastRefreshUtc = DateTime.MinValue;
        private static bool? _lastLiveRefreshSucceeded;
        private static readonly List<Action<bool>> ConnectionWaiters = new List<Action<bool>>();

        internal static bool Enabled => _catalogUri != null;

        /// <summary>
        /// Reports whether the configured endpoint most recently returned a valid live catalogue.
        /// A disk cache deliberately does not count as connected. If the startup refresh is still
        /// running, the callback joins it rather than creating a duplicate request.
        /// </summary>
        internal static void CheckConnection(Action<bool> completed)
        {
            if (completed == null) return;

            bool? immediate = null;
            var startRefresh = false;
            lock (Gate)
            {
                if (_catalogUri == null)
                {
                    immediate = false;
                }
                else if (_refreshing)
                {
                    ConnectionWaiters.Add(completed);
                }
                else if (_lastLiveRefreshSucceeded.HasValue
                         && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
                {
                    immediate = _lastLiveRefreshSucceeded.Value;
                }
                else
                {
                    ConnectionWaiters.Add(completed);
                    startRefresh = true;
                }
            }

            if (immediate.HasValue)
            {
                Dispatcher.NextFrame(() => completed(immediate.Value));
                return;
            }

            if (startRefresh) EnsureRefresh(false);
        }

        internal static void ForceRefresh()
        {
            lock (Gate) _lastRefreshUtc = DateTime.MinValue;
            EnsureRefresh(false);
        }

        internal static void Initialise(UgcStore store, string configuredUrl)
        {
            _store = store;
            lock (Gate)
            {
                ConfirmedPublished.Clear();
                _lastLiveRefreshSucceeded = null;
                _lastRefreshUtc = DateTime.MinValue;
            }
            Uri catalogUri;
            if (store == null || string.IsNullOrWhiteSpace(configuredUrl)) return;
            if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out catalogUri)
                || !string.Equals(catalogUri.Scheme, Uri.UriSchemeHttps,
                                  StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(catalogUri.UserInfo)
                || !string.IsNullOrEmpty(catalogUri.Fragment)
                || !string.IsNullOrEmpty(catalogUri.Query))
            {
                Plugin.Log.LogWarning("Community catalogue URL must be an absolute HTTPS URL "
                                      + "without a query or fragment; community browsing is disabled.");
                return;
            }

            try
            {
                _catalogUri = catalogUri;
                _objectsBaseUri = new Uri(catalogUri, ".");
                var root = Path.GetDirectoryName(store.Root) ?? store.Root;
                var cacheRoot = Path.Combine(root, "catalog-cache");
                _objectCacheDirectory = Path.Combine(cacheRoot, "objects");
                Directory.CreateDirectory(_objectCacheDirectory);
                _catalogCacheFile = Path.Combine(cacheRoot,
                    "catalog-" + Sha256Hex(Encoding.UTF8.GetBytes(catalogUri.AbsoluteUri)).Substring(0, 16)
                    + ".json");
                Plugin.Log.LogInfo("Community catalogue enabled from " + catalogUri.GetLeftPart(UriPartial.Path));
                EnsureRefresh(true);
            }
            catch (Exception ex)
            {
                _catalogUri = null;
                _objectsBaseUri = null;
                Plugin.Log.LogWarning("Could not initialise the community catalogue: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns every matching entry. Paging happens after local and remote results are merged,
        /// otherwise a page from each source would produce duplicates and incorrect page counts.
        /// </summary>
        internal static List<CommunityCatalogEntry> Search(UgcQuery query)
        {
            EnsureRefresh(false);
            CommunityCatalog snapshot;
            lock (Gate) snapshot = _catalog;
            var result = snapshot == null
                ? new List<CommunityCatalogEntry>()
                : snapshot.Search(query);
            for (var i = result.Count - 1; i >= 0; i--)
            {
                if (CommunityCatalog.CompatibilityError(result[i], Plugin.PluginVersion) != null)
                    result.RemoveAt(i);
            }
            return result;
        }

        internal static void NotePublished(string mapId, int revision)
        {
            Guid parsed;
            if (!Guid.TryParse(mapId, out parsed) || revision < 1) return;
            lock (Gate)
            {
                int current;
                var id = parsed.ToString();
                if (!ConfirmedPublished.TryGetValue(id, out current) || revision > current)
                    ConfirmedPublished[id] = revision;
            }
        }

        internal static void NoteRemoved(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return;
            lock (Gate)
            {
                ConfirmedPublished.Remove(mapId);
                if (_catalog != null)
                    for (var i = _catalog.Entries.Count - 1; i >= 0; i--)
                        if (string.Equals(_catalog.Entries[i].Id, mapId,
                                          StringComparison.OrdinalIgnoreCase))
                            _catalog.Entries.RemoveAt(i);
            }
            UgcBackend.MarkCommunityUnpublished(mapId);
            Dispatcher.NextFrame(SharingPatches.RefreshCurrentCreateScreen);
            ForceRefresh();
        }

        internal static List<CommunityCatalogEntry> SearchOwned(UgcQuery query, string creatorId)
        {
            EnsureRefresh(false);
            CommunityCatalog snapshot;
            lock (Gate) snapshot = _catalog;
            var result = snapshot == null
                ? new List<CommunityCatalogEntry>()
                : snapshot.Search(query);
            for (var i = result.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(creatorId) || result[i].CreatorIds == null
                    || !result[i].CreatorIds.Contains(creatorId)
                    || CommunityCatalog.CompatibilityError(result[i], Plugin.PluginVersion) != null)
                    result.RemoveAt(i);
            }
            return result;
        }

        internal static bool IsPublishedMap(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return false;
            lock (Gate)
            {
                if (ConfirmedPublished.ContainsKey(mapId)) return true;
                if (_catalog == null) return false;
                for (var i = 0; i < _catalog.Entries.Count; i++)
                    if (string.Equals(_catalog.Entries[i].Id, mapId,
                                      StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        internal static string ContentKey(CommunityCatalogEntry entry) =>
            KeyPrefix + entry.Id + "/" + entry.Revision + "/bundle";

        internal static string ThumbnailKey(CommunityCatalogEntry entry) =>
            string.IsNullOrEmpty(entry.ThumbnailKey)
                ? string.Empty
                : KeyPrefix + entry.Id + "/" + entry.Revision + "/thumbnail";

        internal static bool OwnsKey(string key) =>
            !string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

        /// <summary>Starts a bounded worker-thread fetch and always completes on Unity's thread.</summary>
        internal static void ReadKey(string key, Action<byte[]> succeeded, Action failed)
        {
            CommunityCatalogEntry entry;
            bool thumbnail;
            if (!TryResolveKey(key, out entry, out thumbnail))
            {
                Dispatcher.NextFrame(failed);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] result = null;
                try
                {
                    result = thumbnail ? ReadThumbnail(entry) : ReadAndInstallBundle(entry);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Community map download failed for " + entry.Id + ": "
                                          + ex.Message);
                }

                Dispatcher.NextFrame(() =>
                {
                    if (result != null)
                    {
                        if (succeeded != null) succeeded(result);
                    }
                    else if (failed != null) failed();
                });
            });
        }

        private static byte[] ReadThumbnail(CommunityCatalogEntry entry)
        {
            var bytes = ReadObject(entry.ThumbnailKey, entry.ThumbnailSizeBytes,
                                   entry.ThumbnailSha256, LevelBundle.MaxImageBytes, ".image");
            if (!LevelBundle.IsSafeGameImage(bytes))
                throw new InvalidDataException("The thumbnail is not a safe game image.");
            return bytes;
        }

        private static byte[] ReadAndInstallBundle(CommunityCatalogEntry entry)
        {
            var incompatibility = CommunityCatalog.CompatibilityError(entry, Plugin.PluginVersion);
            if (incompatibility != null) throw new InvalidDataException(incompatibility);
            var bytes = ReadObject(entry.BundleKey, entry.SizeBytes, entry.Sha256,
                                   LevelBundle.MaxBundleBytes, ".bundle");
            string error;
            var bundle = LevelBundle.Unpack(bytes, out error);
            if (bundle == null) throw new InvalidDataException(error ?? "The level bundle is invalid.");
            if (!string.Equals(bundle.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
                || bundle.ContentVersion != entry.Revision || bundle.ClientVersion != entry.ClientVersion)
            {
                throw new InvalidDataException("The bundle does not match its catalogue entry.");
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
            var installed = _store.Install(prototype, bundle.ClientVersion, bundle.ContentVersion,
                                           bundle.ExportedAtMs, bundle.Content, bundle.ContentImage,
                                           bundle.Thumbnail, out outcome, out error);
            if (installed == null || outcome == UgcInstallOutcome.Conflict
                                  || outcome == UgcInstallOutcome.Failed)
            {
                throw new InvalidDataException(error ?? "The level could not be installed.");
            }

            var latest = installed.LatestContent();
            var content = latest == null ? null : _store.ReadKey(latest.Key);
            if (content == null) throw new IOException("The installed level data could not be read back.");
            Plugin.Log.LogInfo("Community map " + entry.Id + " is ready (" + outcome + ").");
            return content;
        }

        private static byte[] ReadObject(string objectKey, long expectedBytes, string expectedSha,
                                         int maximumBytes, string suffix)
        {
            Uri uri;
            if (!TryResolveObjectUri(objectKey, out uri))
                throw new InvalidDataException("The catalogue contains an unsafe object key.");

            var cached = Path.Combine(_objectCacheDirectory, expectedSha + suffix);
            byte[] bytes;
            lock (ObjectCacheGate) bytes = ReadCacheFile(cached, maximumBytes);
            if (IsExpected(bytes, expectedBytes, expectedSha)) return bytes;

            bytes = Download(uri, maximumBytes);
            if (!IsExpected(bytes, expectedBytes, expectedSha))
                throw new InvalidDataException("The downloaded object does not match its size or SHA-256.");
            lock (ObjectCacheGate)
            {
                var wonRace = ReadCacheFile(cached, maximumBytes);
                if (IsExpected(wonRace, expectedBytes, expectedSha)) return wonRace;
                WriteAtomic(cached, bytes);
                EvictObjectCache();
            }
            return bytes;
        }

        private static bool TryResolveObjectUri(string key, out Uri uri)
        {
            uri = null;
            if (_objectsBaseUri == null || !CommunityCatalog.IsSafeObjectKey(key)) return false;
            Uri candidate;
            if (!Uri.TryCreate(_objectsBaseUri, key, out candidate)) return false;
            if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.Host, _objectsBaseUri.Host,
                                  StringComparison.OrdinalIgnoreCase)
                || candidate.Port != _objectsBaseUri.Port) return false;
            uri = candidate;
            return true;
        }

        private static bool TryResolveKey(string key, out CommunityCatalogEntry entry,
                                          out bool thumbnail)
        {
            entry = null;
            thumbnail = false;
            if (!OwnsKey(key)) return false;
            var parts = key.Substring(KeyPrefix.Length).Split('/');
            Guid id;
            int revision;
            if (parts.Length != 3 || !Guid.TryParse(parts[0], out id)
                || !int.TryParse(parts[1], out revision) || revision < 1
                || (parts[2] != "bundle" && parts[2] != "thumbnail")) return false;

            CommunityCatalog snapshot;
            lock (Gate) snapshot = _catalog;
            if (snapshot == null) return false;
            for (var i = 0; i < snapshot.Entries.Count; i++)
            {
                var candidate = snapshot.Entries[i];
                if (candidate.Revision == revision
                    && string.Equals(candidate.Id, id.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    thumbnail = parts[2] == "thumbnail";
                    if (thumbnail && string.IsNullOrEmpty(candidate.ThumbnailKey)) return false;
                    entry = candidate;
                    return true;
                }
            }
            return false;
        }

        private static void EnsureRefresh(bool loadCacheFirst)
        {
            lock (Gate)
            {
                if (_catalogUri == null || _refreshing
                    || (!loadCacheFirst && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)) return;
                _refreshing = true;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (loadCacheFirst) TryLoadCachedCatalog();
                var liveSucceeded = false;
                try
                {
                    var bytes = Download(_catalogUri, CommunityCatalog.MaxDocumentBytes);
                    CommunityCatalog parsed;
                    string warning;
                    if (!CommunityCatalog.TryParse(bytes, out parsed, out warning))
                        throw new InvalidDataException(warning ?? "The catalogue is invalid.");
                    liveSucceeded = true;
                    lock (Gate) _catalog = parsed;
                    UgcBackend.ReconcileCommunityPublications(parsed.Entries);
                    Dispatcher.NextFrame(SharingPatches.RefreshCurrentCreateScreen);
                    WriteAtomic(_catalogCacheFile, bytes);
                    if (!string.IsNullOrEmpty(warning))
                        Plugin.Log.LogWarning("Community catalogue: " + warning);
                    Plugin.Log.LogInfo("Community catalogue loaded " + parsed.Entries.Count + " map(s)." );
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not refresh the community catalogue; using the "
                                          + "local library/cache: " + ex.Message);
                }
                finally
                {
                    List<Action<bool>> waiters;
                    lock (Gate)
                    {
                        _lastRefreshUtc = DateTime.UtcNow;
                        _lastLiveRefreshSucceeded = liveSucceeded;
                        _refreshing = false;
                        waiters = new List<Action<bool>>(ConnectionWaiters);
                        ConnectionWaiters.Clear();
                    }

                    if (waiters.Count > 0)
                        Dispatcher.NextFrame(() => CompleteConnectionWaiters(waiters, liveSucceeded));
                }
            });
        }

        private static void CompleteConnectionWaiters(List<Action<bool>> waiters, bool connected)
        {
            for (var i = 0; i < waiters.Count; i++)
            {
                try
                {
                    waiters[i](connected);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("A community connection-status callback threw: "
                                          + ex.Message);
                }
            }
        }

        private static void TryLoadCachedCatalog()
        {
            try
            {
                var bytes = ReadCacheFile(_catalogCacheFile, CommunityCatalog.MaxDocumentBytes);
                CommunityCatalog parsed;
                string warning;
                if (CommunityCatalog.TryParse(bytes, out parsed, out warning))
                {
                    lock (Gate) _catalog = parsed;
                    UgcBackend.ReconcileCommunityPublications(parsed.Entries);
                    Dispatcher.NextFrame(SharingPatches.RefreshCurrentCreateScreen);
                    Plugin.Log.LogInfo("Loaded " + parsed.Entries.Count + " cached community map(s)." );
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Ignoring the cached community catalogue: " + ex.Message);
            }
        }

        private static byte[] Download(Uri uri, int maximumBytes)
        {
            var stopwatch = Stopwatch.StartNew();
            var current = uri;
            for (var redirects = 0; redirects <= 4; redirects++)
            {
                var remaining = RequestTimeoutMs - (int)stopwatch.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("The HTTPS request exceeded 10 seconds.");
                var request = (HttpWebRequest)WebRequest.Create(current);
                request.Method = "GET";
                request.AllowAutoRedirect = false;
                request.AutomaticDecompression = DecompressionMethods.None;
                request.Timeout = remaining;
                request.ReadWriteTimeout = remaining;
                request.UserAgent = "SS2Revive/" + Plugin.PluginVersion;

                using (var deadline = new Timer(delegate { try { request.Abort(); } catch { } }, null,
                                                remaining, Timeout.Infinite))
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        Uri next;
                        if (redirects == 4 || !Uri.TryCreate(current, response.Headers["Location"], out next)
                            || !IsAllowedRedirect(current, next))
                            throw new WebException("HTTPS returned an unsafe redirect.");
                        current = next;
                        continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new WebException("HTTPS returned " + status + ".");
                    if (response.ContentLength > maximumBytes)
                        throw new InvalidDataException("The response exceeds its byte limit.");

                    using (var input = response.GetResponseStream())
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[32 * 1024];
                        while (true)
                        {
                            if (stopwatch.ElapsedMilliseconds > RequestTimeoutMs)
                                throw new TimeoutException("The HTTPS request exceeded 10 seconds.");
                            var read = input.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            if (output.Length + read > maximumBytes)
                                throw new InvalidDataException("The response exceeds its byte limit.");
                            output.Write(buffer, 0, read);
                        }
                        return output.ToArray();
                    }
                }
            }
            throw new WebException("HTTPS returned too many redirects.");
        }

        /// <summary>
        /// Keep redirects same-origin. GitHub Releases is the one explicit exception: its stable
        /// github.com asset URLs redirect to GitHub's own immutable content CDN.
        /// </summary>
        private static bool IsAllowedRedirect(Uri from, Uri to)
        {
            if (to == null || !string.Equals(to.Scheme, Uri.UriSchemeHttps,
                                             StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
                && from.Port == to.Port) return true;
            return string.Equals(from.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                   && (string.Equals(to.Host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                       || to.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
        }

        private static byte[] ReadCacheFile(string path, int maximumBytes)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > maximumBytes) return null;
            return File.ReadAllBytes(path);
        }

        private static bool IsExpected(byte[] bytes, long size, string sha) =>
            bytes != null && bytes.LongLength == size
            && string.Equals(Sha256Hex(bytes), sha, StringComparison.OrdinalIgnoreCase);

        private static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var result = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++) result.Append(hash[i].ToString("x2"));
                return result.ToString();
            }
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private static void EvictObjectCache()
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(_objectCacheDirectory).GetFiles());
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                long total = 0;
                for (var i = 0; i < files.Count; i++) total += files[i].Length;
                while (files.Count > MaxCacheFiles || total > MaxCacheBytes)
                {
                    var oldest = files[0];
                    files.RemoveAt(0);
                    total -= oldest.Length;
                    oldest.Delete();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not trim the community-map cache: " + ex.Message);
            }
        }
    }
}
