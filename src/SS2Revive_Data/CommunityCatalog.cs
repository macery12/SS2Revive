using System;
using System.Collections.Generic;
using System.Text;

namespace SS2ReviveData
{
    /// <summary>
    /// One entry in the read-only community catalogue. Object locations are deliberately relative
    /// keys, never URLs: the client resolves them against the configured catalogue directory, so a
    /// catalogue author cannot turn a thumbnail or level request into an arbitrary web request.
    /// </summary>
    public sealed class CommunityCatalogEntry
    {
        public string Id = string.Empty;
        public int Revision;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public List<string> CreatorIds = new List<string>();
        public List<string> Tags = new List<string>();
        public long CreatedAtMs;
        public long UpdatedAtMs;
        public int ClientVersion;
        public int MapFormatVersion;
        public string ReviveVersion = string.Empty;
        public string MinimumReviveVersion = string.Empty;
        public long SizeBytes;
        public string Sha256 = string.Empty;
        public string BundleKey = string.Empty;
        public long ThumbnailSizeBytes;
        public string ThumbnailSha256 = string.Empty;
        public string ThumbnailKey = string.Empty;
        public Json Configurations;
        public Json Validations;

        public UgcLevelRecord ToRecord(string contentKey, string thumbnailKey)
        {
            return new UgcLevelRecord
            {
                Id = Id,
                Title = Title,
                Description = Description,
                Status = UgcStore.StatusPublished,
                CreatorIds = new List<string>(CreatorIds),
                Tags = new List<string>(Tags),
                CreatedAtMs = CreatedAtMs,
                UpdatedAtMs = UpdatedAtMs,
                IsImported = true,
                ThumbnailKey = thumbnailKey ?? string.Empty,
                Configurations = Configurations,
                Validations = Validations,
                Contents = new List<UgcContentRecord>
                {
                    new UgcContentRecord
                    {
                        Id = Id + "-catalog-" + Revision,
                        Key = contentKey ?? string.Empty,
                        CreatedAtMs = UpdatedAtMs,
                        ClientVersion = ClientVersion,
                        ContentVersion = Revision,
                    },
                },
            };
        }
    }

    /// <summary>
    /// Parser and query model for the phase-one static community catalogue. It has no networking
    /// dependency, which keeps the hostile-input rules testable outside Unity.
    /// </summary>
    public sealed class CommunityCatalog
    {
        public const int SchemaVersion = 1;
        public const int MaxDocumentBytes = 2 * 1024 * 1024;
        public const int MaxEntries = 2000;
        public const int MaxObjectKeyCharacters = 256;
        public const int SupportedMapFormatVersion = 29;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public string GeneratedAtUtc = string.Empty;
        public List<CommunityCatalogEntry> Entries = new List<CommunityCatalogEntry>();

        public static bool TryParse(byte[] bytes, out CommunityCatalog catalog, out string error)
        {
            catalog = null;
            error = null;
            if (bytes == null || bytes.Length == 0)
            {
                error = "The catalogue is empty.";
                return false;
            }
            if (bytes.Length > MaxDocumentBytes)
            {
                error = "The catalogue is larger than the 2 MB safety limit.";
                return false;
            }

            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                error = "The catalogue is not valid UTF-8.";
                return false;
            }
            if (!HasSafeNesting(text))
            {
                error = "The catalogue JSON is nested too deeply.";
                return false;
            }

            Json root;
            try { root = Json.Parse(text); }
            catch (Exception ex)
            {
                error = "The catalogue JSON could not be read: " + ex.Message;
                return false;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                error = "The catalogue root must be a JSON object.";
                return false;
            }
            if (root["schemaVersion"] == null || root["schemaVersion"].AsIntOr(0) != SchemaVersion)
            {
                error = "The catalogue schema is not supported by this build.";
                return false;
            }

            var maps = root["maps"];
            if (maps == null || maps.Kind != JsonKind.Array)
            {
                error = "The catalogue has no maps array.";
                return false;
            }
            if (maps.Count > MaxEntries)
            {
                error = "The catalogue has more than " + MaxEntries + " entries.";
                return false;
            }

            var parsed = new CommunityCatalog
            {
                GeneratedAtUtc = Clean(root["generatedAtUtc"].AsStringOr(string.Empty), 64),
            };
            var byId = new Dictionary<string, CommunityCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var rejected = 0;
            foreach (var value in maps.Items)
            {
                CommunityCatalogEntry entry;
                if (!TryReadEntry(value, out entry))
                {
                    rejected++;
                    continue;
                }

                CommunityCatalogEntry prior;
                if (byId.TryGetValue(entry.Id, out prior))
                {
                    if (entry.Revision > prior.Revision) byId[entry.Id] = entry;
                }
                else byId.Add(entry.Id, entry);
            }

            foreach (var pair in byId) parsed.Entries.Add(pair.Value);
            catalog = parsed;
            if (rejected > 0) error = "Ignored " + rejected + " invalid catalogue entr"
                                      + (rejected == 1 ? "y." : "ies.");
            return true;
        }

        /// <summary>Filters without ever putting a remote entry in an author's local Create list.</summary>
        public List<CommunityCatalogEntry> Search(UgcQuery query)
        {
            var result = new List<CommunityCatalogEntry>();
            if (query == null) query = new UgcQuery();

            // Creator-filtered searches are the game's My Levels rows. Catalogue attribution is
            // display-only and must not grant edit ownership, even if a manifest claims the local id.
            if (!string.IsNullOrEmpty(query.CreatorId)) return result;
            if (!query.AnyStatus && !string.IsNullOrEmpty(query.Status)
                && !string.Equals(query.Status, UgcStore.StatusPublished,
                                  StringComparison.OrdinalIgnoreCase)) return result;

            for (var i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                if (!string.IsNullOrEmpty(query.Tag) && !entry.Tags.Contains(query.Tag)) continue;
                if (!string.IsNullOrEmpty(query.TitleContains)
                    && entry.Title.IndexOf(query.TitleContains,
                                           StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (query.Ids != null && query.Ids.Count > 0 && !query.Ids.Contains(entry.Id)) continue;
                if (query.PartySize > 0 && !SupportsPartySize(entry.Configurations, query.PartySize)) continue;
                result.Add(entry);
            }

            result.Sort((a, b) => b.UpdatedAtMs.CompareTo(a.UpdatedAtMs));
            return result;
        }

        /// <summary>Returns a player-facing reason, or null when this build can install the map.</summary>
        public static string CompatibilityError(CommunityCatalogEntry entry, string currentReviveVersion)
        {
            if (entry == null) return "The catalogue entry is missing.";
            if (entry.MapFormatVersion != SupportedMapFormatVersion)
                return "Map format " + entry.MapFormatVersion + " is not supported (this game uses "
                       + SupportedMapFormatVersion + ").";
            if (!string.IsNullOrEmpty(entry.MinimumReviveVersion)
                && CompareVersions(currentReviveVersion, entry.MinimumReviveVersion) < 0)
                return "Requires SS2Revive " + entry.MinimumReviveVersion + " or newer.";
            return null;
        }

        public static bool IsSafeObjectKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxObjectKeyCharacters
                || key[0] == '/' || key[key.Length - 1] == '/' || key.IndexOf("//", StringComparison.Ordinal) >= 0)
                return false;

            var segmentLength = 0;
            for (var i = 0; i < key.Length; i++)
            {
                var c = key[i];
                if (c == '/')
                {
                    if (segmentLength == 1 && key[i - 1] == '.') return false;
                    if (segmentLength == 2 && key[i - 1] == '.' && key[i - 2] == '.') return false;
                    segmentLength = 0;
                    continue;
                }
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')
                    && !(c >= '0' && c <= '9') && c != '-' && c != '_' && c != '.') return false;
                segmentLength++;
            }
            return !(segmentLength == 1 && key[key.Length - 1] == '.')
                   && !(segmentLength == 2 && key[key.Length - 1] == '.' && key[key.Length - 2] == '.');
        }

        private static bool TryReadEntry(Json value, out CommunityCatalogEntry entry)
        {
            entry = null;
            if (value == null || value.Kind != JsonKind.Object) return false;

            Guid id;
            if (!Guid.TryParse(value["id"].AsStringOr(string.Empty), out id)) return false;
            var revision = value["revision"].AsIntOr(0);
            var clientVersion = value["clientVersion"].AsIntOr(0);
            var mapFormatVersion = value["mapFormatVersion"].AsIntOr(clientVersion);
            var minimumReviveVersion = value["minimumReviveVersion"].AsStringOr(string.Empty);
            var size = value["sizeBytes"].AsLongOr(0);
            var sha = value["sha256"].AsStringOr(string.Empty).ToLowerInvariant();
            var bundleKey = value["bundleKey"].AsStringOr(string.Empty);
            if (revision < 1 || clientVersion < 1 || mapFormatVersion < 1
                || clientVersion != mapFormatVersion
                || (!string.IsNullOrEmpty(minimumReviveVersion) && !IsVersion(minimumReviveVersion))
                || size < 1 || size > LevelBundle.MaxBundleBytes
                || !IsSha256(sha) || !IsSafeObjectKey(bundleKey)) return false;

            var thumbnailKey = value["thumbnailKey"].AsStringOr(string.Empty);
            var thumbnailSize = value["thumbnailSizeBytes"].AsLongOr(0);
            var thumbnailSha = value["thumbnailSha256"].AsStringOr(string.Empty).ToLowerInvariant();
            if (!string.IsNullOrEmpty(thumbnailKey)
                && (!IsSafeObjectKey(thumbnailKey) || thumbnailSize < 1
                    || thumbnailSize > LevelBundle.MaxImageBytes || !IsSha256(thumbnailSha))) return false;
            if (string.IsNullOrEmpty(thumbnailKey))
            {
                thumbnailSize = 0;
                thumbnailSha = string.Empty;
            }

            var configurations = value["configurations"];
            var validations = value["validations"];
            if (!IsSafeConfigurations(configurations) || !IsSafeValidations(validations)) return false;

            var normalizedId = id.ToString();
            var code = value["code"].AsStringOr(string.Empty);
            if (!string.IsNullOrEmpty(code)
                && !string.Equals(code, LevelCode.FromLevelId(normalizedId), StringComparison.Ordinal)) return false;

            entry = new CommunityCatalogEntry
            {
                Id = normalizedId,
                Revision = revision,
                Title = Clean(value["title"].AsStringOr("Untitled level"), LevelBundle.MaxTitleCharacters),
                Description = Clean(value["description"].AsStringOr(string.Empty), LevelBundle.MaxDescriptionCharacters),
                CreatedAtMs = SafeTime(value["createdAtMs"].AsLongOr(0)),
                UpdatedAtMs = SafeTime(value["updatedAtMs"].AsLongOr(value["exportedAtMs"].AsLongOr(0))),
                ClientVersion = clientVersion,
                MapFormatVersion = mapFormatVersion,
                ReviveVersion = Clean(value["reviveVersion"].AsStringOr(string.Empty), 64),
                MinimumReviveVersion = minimumReviveVersion,
                SizeBytes = size,
                Sha256 = sha,
                BundleKey = bundleKey,
                ThumbnailSizeBytes = thumbnailSize,
                ThumbnailSha256 = thumbnailSha,
                ThumbnailKey = thumbnailKey,
                Configurations = configurations,
                Validations = validations,
            };
            ReadStrings(value["creatorIds"], entry.CreatorIds, LevelBundle.MaxCreators);
            ReadStrings(value["tags"], entry.Tags, LevelBundle.MaxTags);
            if (entry.UpdatedAtMs == 0) entry.UpdatedAtMs = entry.CreatedAtMs;
            if (string.IsNullOrEmpty(entry.Title)) entry.Title = "Untitled level";
            return true;
        }

        private static void ReadStrings(Json array, List<string> result, int maximum)
        {
            if (array == null || array.Kind != JsonKind.Array) return;
            foreach (var item in array.Items)
            {
                if (result.Count >= maximum) break;
                var text = Clean(item.AsStringOr(string.Empty), LevelBundle.MaxMetadataItemCharacters);
                if (!string.IsNullOrEmpty(text) && !result.Contains(text)) result.Add(text);
            }
        }

        private static bool SupportsPartySize(Json configurations, int partySize)
        {
            if (configurations == null || configurations.Kind != JsonKind.Array || configurations.Count == 0)
                return true;
            foreach (var item in configurations.Items)
            {
                if (item["numberPlayers"].AsIntOr(-1) == partySize) return true;
            }
            return false;
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static bool IsSafeConfigurations(Json configurations)
        {
            if (configurations == null) return true;
            if (configurations.Kind != JsonKind.Array || configurations.Count > 16) return false;
            foreach (var configuration in configurations.Items)
            {
                if (configuration == null || configuration.Kind != JsonKind.Object) return false;
                var teams = configuration["levelTeamConfigurations"];
                if (teams == null) continue;
                if (teams.Kind != JsonKind.Array || teams.Count > 4) return false;
                foreach (var team in teams.Items)
                {
                    if (team == null || team.Kind != JsonKind.Object) return false;
                    var objectives = team["objectives"];
                    var players = team["playersInTeam"];
                    if (objectives != null && (objectives.Kind != JsonKind.Array || objectives.Count > 32))
                        return false;
                    if (players != null && (players.Kind != JsonKind.Array || players.Count > 4))
                        return false;
                    if (objectives != null)
                    {
                        foreach (var objective in objectives.Items)
                        {
                            if (objective.AsStringOr(string.Empty).Length > 128) return false;
                        }
                    }
                }
            }
            return true;
        }

        private static bool IsSafeValidations(Json validations)
        {
            if (validations == null) return true;
            if (validations.Kind != JsonKind.Array || validations.Count > 128) return false;
            foreach (var validation in validations.Items)
            {
                if (validation == null || validation.Kind != JsonKind.Object
                    || validation["id"].AsStringOr(string.Empty).Length > 128
                    || validation["description"].AsStringOr(string.Empty).Length > 512) return false;
            }
            return true;
        }

        private static bool IsVersion(string value)
        {
            int[] ignored;
            return TryVersion(value, out ignored);
        }

        private static bool HasSafeNesting(string text)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{' || c == '[')
                {
                    depth++;
                    if (depth > 64) return false;
                }
                else if (c == '}' || c == ']') depth--;
                if (depth < 0) return false;
            }
            return depth == 0 && !inString;
        }

        private static int CompareVersions(string left, string right)
        {
            int[] a, b;
            if (!TryVersion(left, out a)) return -1;
            if (!TryVersion(right, out b)) return 0;
            for (var i = 0; i < 4; i++)
            {
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            }
            return 0;
        }

        private static bool TryVersion(string value, out int[] parts)
        {
            parts = new int[4];
            if (string.IsNullOrWhiteSpace(value)) return false;
            var dash = value.IndexOf('-');
            if (dash >= 0) value = value.Substring(0, dash);
            var split = value.Split('.');
            if (split.Length < 1 || split.Length > 4) return false;
            for (var i = 0; i < split.Length; i++)
            {
                if (split[i].Length == 0 || !int.TryParse(split[i], out parts[i]) || parts[i] < 0)
                    return false;
            }
            return true;
        }

        private static long SafeTime(long value)
        {
            var now = UgcStore.NowMs();
            return value < 0 || value > now + 24L * 60 * 60 * 1000 ? now : value;
        }

        private static string Clean(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var output = new StringBuilder(Math.Min(value.Length, maximum));
            for (var i = 0; i < value.Length && output.Length < maximum; i++)
            {
                if (value[i] >= ' ') output.Append(value[i]);
            }
            return output.ToString().Trim();
        }
    }
}
