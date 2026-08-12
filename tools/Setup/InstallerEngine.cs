using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SS2Revive.Setup;

internal static class InstallerEngine
{
    internal const string BuildFolderName = "Surgeon Simulator 2 - 1.3.7";
    private const string AppId = "774791";
    private const string DepotId = "774793";
    private const string ManifestId = "5729349529999704019";
    private const string GameExeName = "Surgeon Simulator 2.exe";
    private const string BepInExVersion = "5.4.23.2";
    private const string BepInExUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip";
    private const string DepotDownloaderRepo = "SteamRE/DepotDownloader";
    private const string ModRepo = "macery12/SS2Revive";
    private const long MaxApiResponseBytes = 2 * 1024 * 1024;
    private const long MaxArchiveBytes = 256 * 1024 * 1024;
    private const long MaxExpandedBytes = 768 * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;
    private static readonly HttpClient Http = CreateHttpClient();

    internal sealed record InstallResult(
        string InstallDirectory,
        string GameDirectory,
        string LauncherPath,
        bool ModInstalled,
        string? ModVersion,
        string? ModError);

    private sealed record ReleaseAsset(string Tag, string Name, Uri Url);

    internal static async Task<InstallResult> InstallAsync(
        string installDirectory,
        string steamUsername,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SS2 Revive Setup supports Windows x64 only.");
        if (string.IsNullOrWhiteSpace(steamUsername))
            throw new ArgumentException("A Steam login name is required.", nameof(steamUsername));
        installDirectory = Path.GetFullPath(installDirectory);
        Directory.CreateDirectory(installDirectory);

        var toolRoot = Path.Combine(installDirectory, "_tools", "DepotDownloader");
        var toolExe = Path.Combine(toolRoot, "DepotDownloader.exe");
        if (!File.Exists(toolExe))
        {
            progress.Report("Downloading DepotDownloader…");
            var depotAsset = await GetLatestReleaseAssetAsync(
                DepotDownloaderRepo,
                name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("windows-x64", StringComparison.OrdinalIgnoreCase),
                "a Windows x64 DepotDownloader archive",
                cancellationToken);
            await InstallZipAsync(depotAsset.Url, toolRoot, ["DepotDownloader.exe"], cancellationToken);
        }

        progress.Report("Steam sign-in is opening in DepotDownloader…");
        var exitCode = await RunDepotDownloaderAsync(toolExe, steamUsername, installDirectory, cancellationToken);
        if (exitCode != 0) throw new InvalidOperationException($"DepotDownloader exited with code {exitCode}.");

        progress.Report("Locating and verifying Surgeon Simulator 2 build 1.3.7…");
        var gameExe = Directory.EnumerateFiles(installDirectory, GameExeName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
        if (gameExe is null)
            throw new FileNotFoundException($"The download completed, but {GameExeName} was not found.");
        var gameFolder = Path.GetDirectoryName(gameExe)
                         ?? throw new InvalidDataException("The game executable has no parent directory.");
        await File.WriteAllTextAsync(Path.Combine(gameFolder, "steam_appid.txt"), AppId,
            new UTF8Encoding(false), cancellationToken);

        progress.Report($"Installing BepInEx {BepInExVersion} x64…");
        await InstallZipAsync(
            new Uri(BepInExUrl),
            gameFolder,
            ["winhttp.dll", Path.Combine("BepInEx", "core", "BepInEx.dll")],
            cancellationToken);

        var pluginFolder = Path.Combine(gameFolder, "BepInEx", "plugins", "SS2Revive");
        var modInstalled = false;
        string? modVersion = null;
        string? modError = null;
        progress.Report("Installing the latest published SS2 Revive mod…");
        try
        {
            var modAsset = await GetLatestReleaseAssetAsync(
                ModRepo,
                name => name.StartsWith("SS2Revive-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                "an SS2Revive-*.zip mod archive",
                cancellationToken);
            await InstallZipAsync(
                modAsset.Url,
                pluginFolder,
                ["SS2Revive.dll", "SS2Revive_Data.dll", Path.Combine("newsfeed", "NewsFeed.json")],
                cancellationToken);
            modInstalled = true;
            modVersion = modAsset.Tag;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Directory.CreateDirectory(pluginFolder);
            modError = exception.Message;
        }

        progress.Report("Creating the build 1.3.7 launcher…");
        var launcher = Path.Combine(installDirectory, "Launch Surgeon Simulator 2 - 1.3.7.cmd");
        await File.WriteAllLinesAsync(launcher,
            ["@echo off", $"cd /d \"{EscapeBatchPath(gameFolder)}\"", $"start \"\" \"{EscapeBatchPath(gameExe)}\""],
            Encoding.ASCII,
            cancellationToken);
        progress.Report(modInstalled
            ? "Installation complete."
            : "Game setup complete, but SS2 Revive requires manual installation.");
        return new InstallResult(installDirectory, gameFolder, launcher, modInstalled, modVersion, modError);
    }

    private static async Task<int> RunDepotDownloaderAsync(
        string executable,
        string username,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        foreach (var argument in new[]
                 {
                     "-app", AppId, "-depot", DepotId, "-manifest", ManifestId,
                     "-username", username,
                     "-dir", installDirectory, "-validate",
                 })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("DepotDownloader could not be started.");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return process.ExitCode;
    }

    private static async Task<ReleaseAsset> GetLatestReleaseAssetAsync(
        string repository,
        Func<string, bool> accepts,
        string expected,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(
            $"https://api.github.com/repos/{repository}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new BoundedReadStream(stream, MaxApiResponseBytes);
        using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("GitHub returned a release without a tag.");
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var urlText = asset.GetProperty("browser_download_url").GetString();
            if (name is null || urlText is null || !accepts(name)) continue;
            var url = new Uri(urlText);
            if (url.Scheme != Uri.UriSchemeHttps
                || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub returned an unexpected asset URL.");
            return new ReleaseAsset(tag, name, url);
        }
        throw new InvalidDataException($"Release {tag} has no {expected}.");
    }

    private static async Task InstallZipAsync(
        Uri url,
        string destination,
        IReadOnlyList<string> requiredFiles,
        CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Downloads must use HTTPS.");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ss2revive-setup-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(temporaryRoot, "download.zip");
        var extractedPath = Path.Combine(temporaryRoot, "payload");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await DownloadFileAsync(url, archivePath, cancellationToken);
            ExtractZipPayload(archivePath, extractedPath, requiredFiles);
            CopyTree(extractedPath, destination);
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
        }
    }

    private static async Task DownloadFileAsync(Uri url, string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("A download redirected away from HTTPS.");
        if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
            throw new InvalidDataException("The download is larger than the setup safety limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaxArchiveBytes) throw new InvalidDataException("The download exceeded the setup safety limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new InvalidDataException("The downloaded archive is empty.");
    }

    private static void ExtractZipPayload(string archivePath, string destination, IReadOnlyList<string> requiredFiles)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("The downloaded archive contains too many entries.");
        var markerName = Path.GetFileName(requiredFiles[0]);
        var marker = archive.Entries
            .Where(entry => entry.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"{markerName} was not found in the downloaded archive.");
        var markerPath = marker.FullName.Replace('\\', '/');
        var slash = markerPath.LastIndexOf('/');
        var prefix = slash < 0 ? string.Empty : markerPath[..slash];
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var fullName = entry.FullName.Replace('\\', '/');
            if (prefix.Length > 0)
            {
                if (!fullName.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                fullName = fullName[(prefix.Length + 1)..];
            }
            if (string.IsNullOrEmpty(fullName)) continue;
            if (fullName.StartsWith('/') || Path.IsPathFullyQualified(fullName)
                || fullName.Split('/').Any(part => part is ".." or "."))
                throw new InvalidDataException("The downloaded archive contains an unsafe path.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000) throw new InvalidDataException("The downloaded archive contains a symbolic link.");
            expanded += entry.Length;
            if (entry.Length > MaxExpandedBytes || expanded > MaxExpandedBytes)
                throw new InvalidDataException("The downloaded archive expands beyond the setup safety limit.");
            var outputPath = Path.GetFullPath(Path.Combine(destination,
                fullName.Replace('/', Path.DirectorySeparatorChar)));
            var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded archive escapes its staging directory.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: true);
        }
        foreach (var required in requiredFiles)
            if (!File.Exists(Path.Combine(destination, required)))
                throw new InvalidDataException($"The archive is missing required file {required}.");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string EscapeBatchPath(string path) => path.Replace("%", "%%");

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SS2Revive-Setup", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static void RunSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss2revive-setup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var validZip = Path.Combine(root, "valid.zip");
            using (var zip = ZipFile.Open(validZip, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "wrapper/SS2Revive.dll", "plugin");
                WriteEntry(zip, "wrapper/SS2Revive_Data.dll", "data");
                WriteEntry(zip, "wrapper/newsfeed/NewsFeed.json", "{}");
            }
            var validOutput = Path.Combine(root, "valid-output");
            ExtractZipPayload(validZip, validOutput,
                ["SS2Revive.dll", "SS2Revive_Data.dll", Path.Combine("newsfeed", "NewsFeed.json")]);
            if (File.ReadAllText(Path.Combine(validOutput, "SS2Revive.dll")) != "plugin")
                throw new InvalidOperationException("Valid archive extraction failed.");

            var unsafeZip = Path.Combine(root, "unsafe.zip");
            using (var zip = ZipFile.Open(unsafeZip, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "SS2Revive.dll", "plugin");
                WriteEntry(zip, "../escape.txt", "unsafe");
            }
            try
            {
                ExtractZipPayload(unsafeZip, Path.Combine(root, "unsafe-output"), ["SS2Revive.dll"]);
                throw new InvalidOperationException("Unsafe archive traversal was accepted.");
            }
            catch (InvalidDataException) { }
            if (File.Exists(Path.Combine(root, "escape.txt")))
                throw new InvalidOperationException("Unsafe archive traversal wrote outside staging.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = inner.Read(buffer, offset, count);
            Count(result);
            return result;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(buffer, cancellationToken);
            Count(result);
            return result;
        }
        private void Count(int count)
        {
            _read += count;
            if (_read > maximumBytes) throw new InvalidDataException("The GitHub API response exceeded the safety limit.");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
