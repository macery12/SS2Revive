using System.Globalization;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SS2ReviveData;

namespace SS2Revive.CatalogPublisher;

internal static class Program
{
    private const int MaxCatalogMaps = 2_000;
    // Must match CommunityCatalog.MaxDocumentBytes in the phase-1 client.
    private const int MaxCatalogBytes = 2 * 1024 * 1024;
    private const int MaxOpaqueJsonBytes = 64 * 1024;
    private static readonly string CurrentReviveVersion =
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "SS2ReviveVersion").Value
        ?? throw new InvalidOperationException("SS2ReviveVersion assembly metadata is missing.");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return args.Length == 0 ? 2 : 0;
            }

            return args[0] switch
            {
                "publish" => Publish(ParseOptions(args, 1)),
                "validate" => ValidateCommand(ParseOptions(args, 1)),
                "links" => Links(ParseOptions(args, 1)),
                "self-test" => SelfTest(),
                _ => Fail("Unknown command '" + args[0] + "'. Run with --help for usage."),
            };
        }
        catch (PublisherException exception)
        {
            Console.Error.WriteLine("error: " + exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("unexpected error: " + exception.Message);
            return 1;
        }
    }

    private static int Publish(Dictionary<string, string?> options)
    {
        var inputDirectory = RequiredDirectory(options, "input");
        var outputDirectory = RequiredValue(options, "output");
        var allowEmpty = Flag(options, "allow-empty");
        RejectUnknown(options, "input", "output", "allow-empty", "generated-at", "minimum-revive-version");

        var minimumReviveVersion = options.TryGetValue("minimum-revive-version", out var minimum)
            ? RequiredSemanticVersion(minimum, "--minimum-revive-version")
            : RequiredSemanticVersion(CurrentReviveVersion, "the repository SS2ReviveVersion");

        var generatedAt = options.TryGetValue("generated-at", out var supplied)
            ? ParseTimestamp(supplied!)
            : DateTimeOffset.UtcNow;

        var files = Directory.EnumerateFiles(inputDirectory, "*" + LevelBundle.Extension,
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0 && !allowEmpty)
            throw new PublisherException("No .ss2level bundles found. Pass --allow-empty intentionally to publish an empty catalog.");
        if (files.Count > MaxCatalogMaps)
            throw new PublisherException($"The catalog contains {files.Count} maps; the phase-1 limit is {MaxCatalogMaps}.");

        var outputFullPath = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
            throw new PublisherException("Output path must not exist. The publisher promotes a verified staging tree there atomically.");

        var maps = new List<CatalogMap>(files.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var contentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PublisherException($"'{info.Name}' is a link/reparse point; submissions must be ordinary files.");
            if (info.Length <= 0 || info.Length > LevelBundle.MaxBundleBytes)
                throw new PublisherException($"'{info.Name}' is empty or exceeds the {LevelBundle.MaxBundleBytes}-byte bundle limit.");

            var bytes = File.ReadAllBytes(info.FullName);
            var bundle = LevelBundle.Unpack(bytes, out var error)
                ?? throw new PublisherException($"'{info.Name}' is not publishable: {error}");

            ValidateMetadata(bundle, info.Name);
            var id = new Guid(bundle.Id).ToString();
            var code = LevelCode.FromLevelId(id);
            var hash = Sha256(bytes);

            if (!ids.Add(id))
                throw new PublisherException($"Duplicate map id '{id}'. A catalog may expose only one current revision of a map.");
            if (!codes.Add(code))
                throw new PublisherException($"Duplicate share code '{code}'.");
            if (!hashes.Add(hash))
                throw new PublisherException($"Duplicate bundle SHA-256 '{hash}' (the same bundle was submitted more than once).");
            if (!contentHashes.Add(bundle.ContentSha256))
                throw new PublisherException($"'{info.Name}' repeats level content SHA-256 '{bundle.ContentSha256}' under another submission.");

            var configurations = OpaqueJson(bundle.Configurations, "configurations", info.Name, 8);
            var validations = OpaqueJson(bundle.Validations, "validations", info.Name, 32);
            var key = $"map-{id}-r{bundle.ContentVersion}-{hash}{LevelBundle.Extension}";
            string? thumbnailKey = null;
            string? thumbnailHash = null;
            long? thumbnailSize = null;
            if (bundle.Thumbnail is { Length: > 0 })
            {
                thumbnailHash = Sha256(bundle.Thumbnail);
                thumbnailSize = bundle.Thumbnail.LongLength;
                thumbnailKey = $"thumb-{id}-r{bundle.ContentVersion}-{thumbnailHash}.bin";
            }

            maps.Add(new CatalogMap
            {
                Id = id,
                Code = code,
                Revision = bundle.ContentVersion,
                Title = bundle.Title,
                Description = bundle.Description,
                CreatorIds = bundle.CreatorIds.ToArray(),
                Tags = bundle.Tags.ToArray(),
                CreatedAtMs = bundle.CreatedAtMs,
                UpdatedAtMs = bundle.ExportedAtMs,
                ClientVersion = bundle.ClientVersion,
                MapFormatVersion = bundle.ClientVersion,
                MinimumReviveVersion = minimumReviveVersion,
                ReviveVersion = string.IsNullOrWhiteSpace(bundle.ReviveVersion) ? null : bundle.ReviveVersion,
                SizeBytes = bytes.LongLength,
                Sha256 = hash,
                BundleKey = key,
                ThumbnailKey = thumbnailKey,
                ThumbnailSizeBytes = thumbnailSize,
                ThumbnailSha256 = thumbnailHash,
                Configurations = configurations,
                Validations = validations,
                SourcePath = info.FullName,
                SourceThumbnail = bundle.Thumbnail,
            });
        }

        maps.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            GeneratedAtUtc = generatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Maps = maps,
        };

        ValidateCatalog(catalog, null, verifyAssets: false);
        var json = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
        if (json.Length > MaxCatalogBytes)
            throw new PublisherException($"Generated catalog is {json.Length} bytes; the limit is {MaxCatalogBytes}.");

        var parentDirectory = Path.GetDirectoryName(outputFullPath);
        var outputName = Path.GetFileName(outputFullPath);
        if (string.IsNullOrEmpty(parentDirectory) || string.IsNullOrEmpty(outputName))
            throw new PublisherException("Output must name a new directory below an existing parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(parentDirectory,
            "." + outputName + ".staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (var map in maps)
            {
                var destination = SafeAssetPath(stagingDirectory, map.BundleKey);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(map.SourcePath!, destination, overwrite: false);
                if (map.ThumbnailKey is not null)
                {
                    var thumbnailDestination = SafeAssetPath(stagingDirectory, map.ThumbnailKey);
                    File.WriteAllBytes(thumbnailDestination, map.SourceThumbnail!);
                }
            }

            File.WriteAllBytes(Path.Combine(stagingDirectory, "catalog.json"), AppendNewline(json));
            ValidateCatalog(catalog, stagingDirectory, verifyAssets: true);
            Directory.Move(stagingDirectory, outputFullPath);
        }
        finally
        {
            try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true); }
            catch { /* A staging cleanup problem does not hide the original validation error. */ }
        }

        Console.WriteLine($"Published {maps.Count} map(s) to {outputFullPath}");
        foreach (var map in maps)
            Console.WriteLine($"  {map.Code}  r{map.Revision}  {map.Title}");
        return 0;
    }

    private static int ValidateCommand(Dictionary<string, string?> options)
    {
        var catalogPath = RequiredFile(options, "catalog");
        var assetsRoot = options.TryGetValue("assets-root", out var root) && !string.IsNullOrWhiteSpace(root)
            ? Path.GetFullPath(root)
            : Path.GetDirectoryName(catalogPath)!;
        RejectUnknown(options, "catalog", "assets-root");

        var catalog = LoadCatalog(catalogPath);
        ValidateCatalog(catalog, assetsRoot, verifyAssets: true);
        Console.WriteLine($"Valid catalog: {catalog.Maps.Count} map(s), all assets present and hashed.");
        return 0;
    }

    private static int Links(Dictionary<string, string?> options)
    {
        var catalogPath = RequiredFile(options, "catalog");
        var catalogUrlText = RequiredValue(options, "catalog-url");
        RejectUnknown(options, "catalog", "catalog-url");

        if (!Uri.TryCreate(catalogUrlText, UriKind.Absolute, out var catalogUrl)
            || (catalogUrl.Scheme != Uri.UriSchemeHttps && catalogUrl.Scheme != Uri.UriSchemeHttp))
            throw new PublisherException("--catalog-url must be an absolute HTTP(S) URL.");

        var catalog = LoadCatalog(catalogPath);
        ValidateCatalog(catalog, null, verifyAssets: false);
        var baseUrl = new Uri(catalogUrl, ".");
        foreach (var map in catalog.Maps)
        {
            var objectUrl = new Uri(baseUrl, map.BundleKey);
            Console.WriteLine($"{map.Code}\t{objectUrl.AbsoluteUri}\t{map.Title}");
        }
        return 0;
    }

    private static void ValidateMetadata(LevelBundle bundle, string fileName)
    {
        if (!Guid.TryParse(bundle.Id, out _))
            throw new PublisherException($"'{fileName}' has an invalid map id.");
        if (LevelCode.FromLevelId(bundle.Id).Length != LevelCode.Length)
            throw new PublisherException($"'{fileName}' cannot produce a valid share code.");
        if (string.IsNullOrWhiteSpace(bundle.Title) || bundle.Title.Length > LevelBundle.MaxTitleCharacters)
            throw new PublisherException($"'{fileName}' has an empty or oversized title.");
        if (bundle.Description.Length > LevelBundle.MaxDescriptionCharacters)
            throw new PublisherException($"'{fileName}' has an oversized description.");
        if (bundle.ContentVersion < 1)
            throw new PublisherException($"'{fileName}' has an invalid revision.");
        if (bundle.ClientVersion != 29)
            throw new PublisherException($"'{fileName}' uses unsupported map format {bundle.ClientVersion}; expected 29.");
        if (bundle.CreatedAtMs < 0 || bundle.ExportedAtMs < bundle.CreatedAtMs)
            throw new PublisherException($"'{fileName}' has inconsistent timestamps.");
        if (bundle.CreatorIds.Count > LevelBundle.MaxCreators || bundle.Tags.Count > LevelBundle.MaxTags)
            throw new PublisherException($"'{fileName}' has too many creators or tags.");
        if (!string.IsNullOrEmpty(bundle.ReviveVersion)
            && (bundle.ReviveVersion.Length > 64 || HasUnsafeControl(bundle.ReviveVersion)))
            throw new PublisherException($"'{fileName}' has invalid reviveVersion provenance metadata.");

        ValidateStringList(bundle.CreatorIds, "creator", fileName);
        ValidateStringList(bundle.Tags, "tag", fileName);
    }

    private static void ValidateStringList(IReadOnlyList<string> values, string name, string fileName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > LevelBundle.MaxMetadataItemCharacters)
                throw new PublisherException($"'{fileName}' has an empty or oversized {name}.");
            if (!seen.Add(value))
                throw new PublisherException($"'{fileName}' repeats {name} '{value}'.");
        }
    }

    private static JsonElement OpaqueJson(SS2ReviveData.Json? value, string field, string fileName, int maxItems)
    {
        var text = value?.ToString() ?? "[]";
        if (Encoding.UTF8.GetByteCount(text) > MaxOpaqueJsonBytes)
            throw new PublisherException($"'{fileName}' has oversized {field} metadata.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new PublisherException($"'{fileName}' has non-array {field} metadata.");
        if (document.RootElement.GetArrayLength() > maxItems)
            throw new PublisherException($"'{fileName}' has too many {field} entries (maximum {maxItems}).");
        return document.RootElement.Clone();
    }

    private static void ValidateCatalog(CatalogDocument catalog, string? assetsRoot, bool verifyAssets)
    {
        if (catalog.SchemaVersion != 1)
            throw new PublisherException($"Unsupported schemaVersion {catalog.SchemaVersion}; expected 1.");
        if (!DateTimeOffset.TryParseExact(catalog.GeneratedAtUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new PublisherException("generatedAtUtc must be UTC in yyyy-MM-ddTHH:mm:ssZ form.");
        if (catalog.Maps is null || catalog.Maps.Count > MaxCatalogMaps)
            throw new PublisherException($"maps must contain no more than {MaxCatalogMaps} entries.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var map in catalog.Maps)
        {
            if (string.IsNullOrEmpty(map.Id) || !Guid.TryParse(map.Id, out var parsed) || parsed.ToString() != map.Id)
                throw new PublisherException($"Catalog map id '{map.Id}' is not a canonical lowercase GUID.");
            if (!ids.Add(map.Id)) throw new PublisherException($"Duplicate map id '{map.Id}'.");
            if (string.IsNullOrEmpty(map.Code) || map.Code != LevelCode.FromLevelId(map.Id) || !codes.Add(map.Code))
                throw new PublisherException($"Map '{map.Id}' has an invalid or duplicate code.");
            if (map.Revision < 1 || map.ClientVersion != 29 || map.MapFormatVersion != 29
                || map.MapFormatVersion != map.ClientVersion)
                throw new PublisherException($"Map '{map.Id}' has an invalid revision or map format version.");
            RequiredSemanticVersion(map.MinimumReviveVersion, $"Map '{map.Id}' minimumReviveVersion");
            if (map.ReviveVersion is not null
                && (map.ReviveVersion.Length is < 1 or > 64 || HasUnsafeControl(map.ReviveVersion)))
                throw new PublisherException($"Map '{map.Id}' has invalid reviveVersion provenance metadata.");
            if (string.IsNullOrWhiteSpace(map.Title) || map.Title.Length > LevelBundle.MaxTitleCharacters
                || map.Description is null || map.Description.Length > LevelBundle.MaxDescriptionCharacters)
                throw new PublisherException($"Map '{map.Id}' has invalid display metadata.");
            if (map.CreatorIds is null || map.Tags is null
                || map.CreatorIds.Length > LevelBundle.MaxCreators || map.Tags.Length > LevelBundle.MaxTags)
                throw new PublisherException($"Map '{map.Id}' has too many creators or tags.");
            ValidateStringList(map.CreatorIds, "creator", map.Id);
            ValidateStringList(map.Tags, "tag", map.Id);
            if (map.CreatedAtMs < 0 || map.UpdatedAtMs < map.CreatedAtMs || map.SizeBytes <= 0
                || map.SizeBytes > LevelBundle.MaxBundleBytes)
                throw new PublisherException($"Map '{map.Id}' has invalid timestamps or size.");
            if (map.Sha256 is null || map.Sha256.Length != 64
                || map.Sha256.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c))
                || !hashes.Add(map.Sha256))
                throw new PublisherException($"Map '{map.Id}' has an invalid or duplicate SHA-256.");
            var expectedKey = $"map-{map.Id}-r{map.Revision}-{map.Sha256}{LevelBundle.Extension}";
            if (string.IsNullOrEmpty(map.BundleKey) || map.BundleKey != expectedKey || !keys.Add(map.BundleKey))
                throw new PublisherException($"Map '{map.Id}' has a mutable, unsafe, or duplicate bundleKey.");
            ValidateThumbnailMetadata(map, keys);
            ValidateOpaqueElement(map.Configurations, "configurations", map.Id, 8);
            ValidateOpaqueElement(map.Validations, "validations", map.Id, 32);

            if (!verifyAssets) continue;
            if (assetsRoot is null) throw new PublisherException("An assets root is required for asset verification.");
            var path = SafeAssetPath(assetsRoot, map.BundleKey);
            if (!File.Exists(path)) throw new PublisherException($"Missing asset '{map.BundleKey}'.");
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PublisherException($"Asset '{map.BundleKey}' is a link/reparse point.");
            if (info.Length != map.SizeBytes)
                throw new PublisherException($"Asset '{map.BundleKey}' size does not match the catalog.");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (actual != map.Sha256)
                throw new PublisherException($"Asset '{map.BundleKey}' SHA-256 does not match the catalog.");
            var bundle = LevelBundle.Unpack(File.ReadAllBytes(path), out var error)
                ?? throw new PublisherException($"Asset '{map.BundleKey}' no longer validates: {error}");
            if (bundle.Id != map.Id || bundle.ContentVersion != map.Revision
                || bundle.ClientVersion != map.ClientVersion || bundle.Code != map.Code)
                throw new PublisherException($"Asset '{map.BundleKey}' identity/revision metadata does not match the catalog.");

            if (map.ThumbnailKey is not null)
            {
                var thumbnailPath = SafeAssetPath(assetsRoot, map.ThumbnailKey);
                if (!File.Exists(thumbnailPath)) throw new PublisherException($"Missing thumbnail '{map.ThumbnailKey}'.");
                var thumbnailInfo = new FileInfo(thumbnailPath);
                if ((thumbnailInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PublisherException($"Thumbnail '{map.ThumbnailKey}' is a link/reparse point.");
                if (thumbnailInfo.Length != map.ThumbnailSizeBytes)
                    throw new PublisherException($"Thumbnail '{map.ThumbnailKey}' size does not match the catalog.");
                var thumbnailBytes = File.ReadAllBytes(thumbnailPath);
                if (Sha256(thumbnailBytes) != map.ThumbnailSha256)
                    throw new PublisherException($"Thumbnail '{map.ThumbnailKey}' SHA-256 does not match the catalog.");
                if (!LevelBundle.IsSafeGameImage(thumbnailBytes))
                    throw new PublisherException($"Thumbnail '{map.ThumbnailKey}' is not a safe game image envelope.");
            }
        }
    }

    private static void ValidateThumbnailMetadata(CatalogMap map, HashSet<string> keys)
    {
        var fieldCount = (map.ThumbnailKey is null ? 0 : 1)
            + (map.ThumbnailSizeBytes is null ? 0 : 1)
            + (map.ThumbnailSha256 is null ? 0 : 1);
        if (fieldCount == 0) return;
        if (fieldCount != 3)
            throw new PublisherException($"Map '{map.Id}' must provide all thumbnail fields or none of them.");
        if (map.ThumbnailSizeBytes < 1 || map.ThumbnailSizeBytes > LevelBundle.MaxImageBytes)
            throw new PublisherException($"Map '{map.Id}' has an invalid thumbnailSizeBytes.");
        if (map.ThumbnailSha256!.Length != 64
            || map.ThumbnailSha256.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
            throw new PublisherException($"Map '{map.Id}' has an invalid thumbnailSha256.");
        var expected = $"thumb-{map.Id}-r{map.Revision}-{map.ThumbnailSha256}.bin";
        if (map.ThumbnailKey != expected || !keys.Add(map.ThumbnailKey))
            throw new PublisherException($"Map '{map.Id}' has a mutable, unsafe, or duplicate thumbnailKey.");
    }

    private static CatalogDocument LoadCatalog(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaxCatalogBytes)
            throw new PublisherException($"Catalog must contain 1 to {MaxCatalogBytes} bytes.");
        return JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new PublisherException("Catalog JSON is empty.");
    }

    private static void ValidateOpaqueElement(JsonElement value, string name, string id, int maxItems)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new PublisherException($"Map '{id}' {name} must be an array.");
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxOpaqueJsonBytes)
            throw new PublisherException($"Map '{id}' {name} exceeds {MaxOpaqueJsonBytes} bytes.");
        if (value.GetArrayLength() > maxItems)
            throw new PublisherException($"Map '{id}' {name} contains more than {maxItems} entries.");
    }

    private static string SafeAssetPath(string root, string key)
    {
        if (Path.IsPathRooted(key) || key.Contains('\\') || key.Split('/').Any(part => part is "" or "." or ".."))
            throw new PublisherException($"Unsafe asset key '{key}'.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new PublisherException($"Asset key '{key}' escapes the assets root.");
        return fullPath;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] AppendNewline(byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        result[^1] = (byte)'\n';
        return result;
    }

    private static int SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss2revive-catalog-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Path.Combine(root, "input");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(input);
            var content = new byte[64];
            Encoding.ASCII.GetBytes("Surgeons").CopyTo(content, 0);
            content[8] = 29;
            var bundle = new LevelBundle
            {
                Id = "1a658233-92c5-4b63-87fc-4740c855730b",
                Title = "Publisher self-test",
                Description = "A generated test bundle.",
                CreatorIds = ["self-test"],
                Tags = ["TEAM_COOP"],
                CreatedAtMs = 1_700_000_000_000,
                ExportedAtMs = 1_700_000_001_000,
                ClientVersion = 29,
                ContentVersion = 3,
                Content = content,
                Thumbnail = GameImageBytes(),
            };
            File.WriteAllBytes(Path.Combine(input, "test.ss2level"), bundle.Pack());
            var result = Publish(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["input"] = input,
                ["output"] = output,
                ["generated-at"] = "2026-01-02T03:04:05Z",
                ["minimum-revive-version"] = "1.0.0",
            });
            if (result != 0) throw new PublisherException("Self-test publish returned a failure.");
            ValidateCommand(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["catalog"] = Path.Combine(output, "catalog.json"),
                ["assets-root"] = output,
            });

            File.Copy(Path.Combine(input, "test.ss2level"), Path.Combine(input, "duplicate.ss2level"));
            ExpectPublisherFailure("duplicate submission", () => Publish(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["input"] = input,
                ["output"] = Path.Combine(root, "duplicate-output"),
            }));
            File.Delete(Path.Combine(input, "duplicate.ss2level"));

            var objectPath = Directory.EnumerateFiles(output, "*.ss2level", SearchOption.TopDirectoryOnly).Single();
            var tampered = File.ReadAllBytes(objectPath);
            var originalObject = (byte[])tampered.Clone();
            tampered[^1] ^= 0xff;
            File.WriteAllBytes(objectPath, tampered);
            ExpectPublisherFailure("tampered object", () => ValidateCommand(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["catalog"] = Path.Combine(output, "catalog.json"),
                ["assets-root"] = output,
            }));
            File.WriteAllBytes(objectPath, originalObject);

            var thumbnailPath = Directory.EnumerateFiles(output, "thumb-*.bin", SearchOption.TopDirectoryOnly).Single();
            var tamperedThumbnail = File.ReadAllBytes(thumbnailPath);
            tamperedThumbnail[^1] ^= 0xff;
            File.WriteAllBytes(thumbnailPath, tamperedThumbnail);
            ExpectPublisherFailure("tampered thumbnail", () => ValidateCommand(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["catalog"] = Path.Combine(output, "catalog.json"),
                ["assets-root"] = output,
            }));

            Console.WriteLine("Self-test passed.");
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* A temp cleanup failure is not a publishing failure. */ }
        }
    }

    private static void ExpectPublisherFailure(string scenario, Action action)
    {
        try
        {
            action();
        }
        catch (PublisherException exception)
        {
            Console.WriteLine($"Rejected {scenario} as expected: {exception.Message}");
            return;
        }
        throw new PublisherException($"Self-test expected the {scenario} scenario to fail.");
    }

    private static byte[] GameImageBytes()
    {
        var bytes = new byte[16];
        var bit = 0;
        WriteBits(bytes, ref bit, 1, 31); // width
        WriteBits(bytes, ref bit, 1, 31); // height
        WriteBits(bytes, ref bit, 3, 4);  // TextureFormat.RGB24
        WriteBits(bytes, ref bit, 3, 31); // byte length: one RGB24 pixel
        bit = (bit + 7) & ~7;
        WriteBits(bytes, ref bit, 0x7f, 8);
        WriteBits(bytes, ref bit, 0x55, 8);
        WriteBits(bytes, ref bit, 0x22, 8);
        return bytes;
    }

    private static void WriteBits(byte[] bytes, ref int bitOffset, uint value, int count)
    {
        for (var i = 0; i < count; i++, bitOffset++)
        {
            if (((value >> i) & 1) != 0)
                bytes[bitOffset / 8] |= (byte)(1 << (bitOffset % 8));
        }
    }

    private static Dictionary<string, string?> ParseOptions(string[] args, int start)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = start; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length == 2)
                throw new PublisherException("Expected an option beginning with --, got '" + args[i] + "'.");
            var key = args[i][2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            if (!result.TryAdd(key, value)) throw new PublisherException("Option --" + key + " was supplied twice.");
        }
        return result;
    }

    private static bool Flag(Dictionary<string, string?> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return false;
        if (value is not null) throw new PublisherException("--" + key + " does not accept a value.");
        return true;
    }

    private static string RequiredValue(Dictionary<string, string?> options, string key)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new PublisherException("Missing required --" + key + " value.");
        return value;
    }

    private static string RequiredDirectory(Dictionary<string, string?> options, string key)
    {
        var path = Path.GetFullPath(RequiredValue(options, key));
        if (!Directory.Exists(path)) throw new PublisherException("Directory does not exist: " + path);
        return path;
    }

    private static string RequiredFile(Dictionary<string, string?> options, string key)
    {
        var path = Path.GetFullPath(RequiredValue(options, key));
        if (!File.Exists(path)) throw new PublisherException("File does not exist: " + path);
        return path;
    }

    private static void RejectUnknown(Dictionary<string, string?> options, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        var unknown = options.Keys.FirstOrDefault(key => !set.Contains(key));
        if (unknown is not null) throw new PublisherException("Unknown option --" + unknown + ".");
    }

    private static DateTimeOffset ParseTimestamp(string text)
    {
        if (!DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
            throw new PublisherException("--generated-at must use UTC form yyyy-MM-ddTHH:mm:ssZ.");
        return timestamp;
    }

    private static string RequiredSemanticVersion(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new PublisherException(source + " is missing.");
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 9
                || part.Any(character => character < '0' || character > '9')
                || (part.Length > 1 && part[0] == '0')))
            throw new PublisherException(source + " must use numeric major.minor.patch form (for example 1.2.3).");
        return text;
    }

    private static bool HasUnsafeControl(string text)
    {
        return text.Any(character => char.IsControl(character));
    }

    private static bool IsHelp(string value) => value is "help" or "--help" or "-h";

    private static int Fail(string message) { throw new PublisherException(message); }

    private static void PrintHelp()
    {
        Console.WriteLine("""
SS2Revive static catalog publisher

Commands:
  publish  --input DIR --output DIR [--generated-at UTC]
           [--minimum-revive-version X.Y.Z] [--allow-empty]
  validate --catalog FILE [--assets-root DIR]
  links    --catalog FILE --catalog-url HTTPS_URL
  self-test

publish validates every .ss2level in DIR, then creates catalog.json and immutable
objects in a fresh output directory. links prints TSV: game share code, direct
bundle URL, and title. It does not register a custom URL protocol.
""");
    }
}

internal sealed class CatalogDocument
{
    public int SchemaVersion { get; set; }
    public string GeneratedAtUtc { get; set; } = "";
    public List<CatalogMap> Maps { get; set; } = [];
}

internal sealed class CatalogMap
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public int Revision { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] CreatorIds { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
    public int ClientVersion { get; set; }
    public int MapFormatVersion { get; set; }
    public string MinimumReviveVersion { get; set; } = "";
    public string? ReviveVersion { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string BundleKey { get; set; } = "";
    public string? ThumbnailKey { get; set; }
    public long? ThumbnailSizeBytes { get; set; }
    public string? ThumbnailSha256 { get; set; }
    public JsonElement Configurations { get; set; }
    public JsonElement Validations { get; set; }

    [JsonIgnore]
    public string? SourcePath { get; set; }

    [JsonIgnore]
    public byte[]? SourceThumbnail { get; set; }
}

internal sealed class PublisherException(string message) : Exception(message);
