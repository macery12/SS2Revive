using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private const int MaxCapturedDepotOutputChars = 128 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    internal sealed record InstallResult(
        string InstallDirectory,
        string GameDirectory,
        string LauncherPath,
        bool ModInstalled,
        string? ModVersion,
        string? ModError);

    private sealed record ReleaseAsset(string Tag, string Name, Uri Url);

    private sealed record DepotDownloaderResult(int ExitCode, string Output, string LogPath);

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
        var depotLogPath = Path.Combine(toolRoot, "DepotDownloader-last-run.log");
        var depotResult = await RunDepotDownloaderAsync(
            toolExe, steamUsername, installDirectory, depotLogPath, cancellationToken);
        if (depotResult.ExitCode != 0)
            throw new InvalidOperationException(BuildDepotDownloaderFailureMessage(depotResult));

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

    private static async Task<DepotDownloaderResult> RunDepotDownloaderAsync(
        string executable,
        string username,
        string installDirectory,
        string logPath,
        CancellationToken cancellationToken)
    {
        using var console = DepotConsoleSession.Open();
        console.Output.WriteLine("SS2 Revive Setup - Steam sign-in");
        console.Output.WriteLine();
        console.Output.WriteLine("Follow the DepotDownloader prompts below to sign in to Steam.");
        console.Output.WriteLine("Your password is entered directly into DepotDownloader; Setup does not read or store it.");
        console.Output.WriteLine("The Steam account must own Surgeon Simulator 2.");
        console.Output.WriteLine("To stop, use Cancel in the Setup window.");
        console.Output.WriteLine();

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // DepotDownloader writes in the console's code page; match it when Setup set it to UTF-8.
            StandardOutputEncoding = console.ChildOutputEncoding,
            StandardErrorEncoding = console.ChildOutputEncoding,
        };
        foreach (var argument in new[]
                 {
                     "-app", AppId, "-depot", DepotId, "-manifest", ManifestId,
                     "-username", username,
                     "-dir", installDirectory, "-validate",
                 })
            start.ArgumentList.Add(argument);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        await log.WriteLineAsync($"SS2 Revive Setup DepotDownloader log - {DateTimeOffset.Now:O}");
        await log.WriteLineAsync($"App {AppId}, depot {DepotId}, manifest {ManifestId}");
        await log.WriteLineAsync("Your Steam password and Steam Guard codes are typed into DepotDownloader and are not recorded here.");
        await log.WriteLineAsync("DepotDownloader's own output, recorded below, includes the Steam login name.");
        await log.WriteLineAsync();

        Process process;
        try
        {
            process = Process.Start(start)
                      ?? throw new InvalidOperationException("Windows did not return a DepotDownloader process.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "DepotDownloader could not be started. Security software may have blocked it, or its files may be damaged. " +
                "Delete the _tools\\DepotDownloader folder and run Setup again.\r\n\r\nWindows reported: " + exception.Message,
                exception);
        }

        using (process)
        {
            var capturedOutput = new RollingTextBuffer(MaxCapturedDepotOutputChars);
            var writeLock = new object();
            var stdout = PumpDepotOutputAsync(process.StandardOutput, console.Output, log, capturedOutput, writeLock);
            var stderr = PumpDepotOutputAsync(process.StandardError, console.Error, log, capturedOutput, writeLock);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAll(stdout, stderr);
                throw;
            }

            await Task.WhenAll(stdout, stderr);
            return new DepotDownloaderResult(process.ExitCode, capturedOutput.ToString(), logPath);
        }
    }

    private static async Task PumpDepotOutputAsync(
        StreamReader input,
        TextWriter console,
        TextWriter log,
        RollingTextBuffer capturedOutput,
        object writeLock)
    {
        var buffer = new char[4096];
        while (true)
        {
            // Off the UI thread: a validated download prints a line per file.
            var read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return;
            lock (writeLock)
            {
                capturedOutput.Append(buffer.AsSpan(0, read));
                console.Write(buffer, 0, read);
                log.Write(buffer, 0, read);
            }
        }
    }

    private sealed record DepotFailureKind(string Explanation, string NextStep, string[] Signals);

    private static readonly DepotFailureKind DepotOwnership = new(
        "The signed-in Steam account does not own Surgeon Simulator 2 or does not have access to its game files.",
        "Sign in with the Steam account that owns Surgeon Simulator 2, then run Setup again. Family Sharing or a free-weekend license may not provide depot access.",
        ["not available from this account", "does not own", "doesn't own", "no license",
         "account has no license", "account does not have access"]);

    private static readonly DepotFailureKind DepotStorage = new(
        "DepotDownloader could not write the game files to the selected location.",
        "Choose a writable folder with enough free space, close programs using that folder, and retry.",
        ["access is denied", "permission denied", "unauthorizedaccessexception", "disk full",
         "not enough space", "no space left", "unable to create install directories",
         "being used by another process", "failed to allocate file", "failed to resize file"]);

    private static readonly DepotFailureKind DepotAccess = new(
        "Steam denied access to the required depot or old build manifest.",
        "Confirm that this Steam account owns the game. If it does, retry later; Steam may be temporarily refusing access to the old build.",
        ["encountered 401", "encountered 403", "encountered 404 for depot manifest", "unable to download manifest",
         "no manifest request code", "manifest is not available", "no valid depot key", "unable to get depot key"]);

    private static readonly DepotFailureKind DepotNetwork = new(
        "DepotDownloader could not reliably connect to Steam.",
        "Check the internet connection, VPN/proxy, firewall, and Steam service status, then retry.",
        ["could not connect to steam", "connection to steam failed", "lost connection to steam",
         "timed out", "timeout", "unable to connect", "failed to connect", "connection refused",
         "connection reset", "name resolution", "network is unreachable", "no such host",
         "serviceunavailable", "failed to find any server"]);

    private static readonly DepotFailureKind DepotSignIn = new(
        "Steam sign-in failed or Steam Guard approval was not completed.",
        "Check the Steam login name, password, and Steam Guard prompt. Wait a few minutes before retrying if Steam reported a rate limit.",
        ["unable to login to steam3", "unable to get steam3 credentials", "invalidpassword", "invalid password",
         "accountlogindenied", "twofactorcodemismatch", "invalidloginauthcode", "logon failed", "failed to log on",
         "failed to authenticate", "ratelimit", "rate limit"]);

    private static readonly DepotFailureKind DepotInterrupted = new(
        "DepotDownloader was interrupted before it finished (Ctrl+C, or its window was closed).",
        "Run Setup again. It resumes and validates the files already downloaded.",
        []);

    private static readonly DepotFailureKind DepotUnknown = new(
        "DepotDownloader reported an error that Setup could not classify automatically.",
        "Read the DepotDownloader details below, correct the reported problem, and run Setup again.",
        []);

    // Checked in this order against a single line, most specific first.
    private static readonly DepotFailureKind[] DepotFailureKinds =
        [DepotOwnership, DepotStorage, DepotAccess, DepotNetwork, DepotSignIn];

    private static readonly string[] DepotErrorWords =
    [
        "error", "fail", "exception", "denied", "unable", "not available", "invalid", "could not",
        "couldn't", "no valid", "encountered", "aborting", "not enough space", "lost connection",
        "timeout", "timed out", "cancel",
    ];

    /// <summary>NTSTATUS a console process exits with when Ctrl+C or Ctrl+Break ends it.</summary>
    private const int StatusControlCExit = unchecked((int)0xC000013A);

    private static string BuildDepotDownloaderFailureMessage(DepotDownloaderResult result)
    {
        var lines = DepotOutputLines(StripTerminalFormatting(result.Output));
        var kind = result.ExitCode == StatusControlCExit ? DepotInterrupted : ClassifyDepotFailure(lines);

        return
            "DepotDownloader could not download Surgeon Simulator 2.\r\n\r\n" +
            kind.Explanation + "\r\n\r\n" +
            "What to do:\r\n" + kind.NextStep + "\r\n\r\n" +
            "DepotDownloader details:\r\n" + GetDepotErrorDetails(lines) + "\r\n\r\n" +
            $"Exit code: {result.ExitCode}\r\n" +
            "Full diagnostic log:\r\n" + result.LogPath;
    }

    /// <summary>
    /// Only error lines count, and the latest one decides. A successful run also prints "This
    /// account is protected by Steam Guard.", "Got depot key ... result: OK" and connection retries
    /// that later recovered, so matching anywhere in the output blames whatever happened first.
    /// "InitializeSteam failed" only says sign-in did not complete; the error before it says why.
    /// </summary>
    private static DepotFailureKind ClassifyDepotFailure(string[] lines)
    {
        var errors = lines.Where(IsDepotErrorLine).Select(line => line.ToLowerInvariant()).ToArray();
        for (var i = errors.Length - 1; i >= 0; i--)
        {
            if (errors[i].Contains("initializesteam failed")) continue;
            var kind = DepotFailureKinds.FirstOrDefault(candidate => ContainsAny(errors[i], candidate.Signals));
            if (kind != null) return kind;
        }

        return errors.Any(line => line.Contains("initializesteam failed")) ? DepotSignIn : DepotUnknown;
    }

    private static string[] DepotOutputLines(string output) =>
        output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private static bool IsDepotErrorLine(string line) => ContainsAny(line.ToLowerInvariant(), DepotErrorWords);

    private static string GetDepotErrorDetails(string[] lines)
    {
        if (lines.Length == 0)
            return "No diagnostic text was captured. The DepotDownloader window may have been closed before it finished.";

        var important = lines.Where(IsDepotErrorLine).TakeLast(4);
        var selected = important
            .Concat(lines.TakeLast(6))
            .Distinct(StringComparer.Ordinal)
            .TakeLast(8)
            .Select(line => line.Length <= 200 ? line : line[..197] + "...");
        return string.Join("\r\n", selected);
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private static string StripTerminalFormatting(string value)
    {
        var withoutAnsi = Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        return new string(withoutAnsi.Where(character =>
            character is '\r' or '\n' or '\t' || !char.IsControl(character)).ToArray());
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

            var ownershipMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                1,
                "App 774791 (Surgeon Simulator 2) is not available from this account.",
                Path.Combine(root, "ownership.log")));
            AssertContains(ownershipMessage, "does not own Surgeon Simulator 2", "ownership failure");
            AssertContains(ownershipMessage, "not available from this account", "DepotDownloader details");

            var authenticationMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                1,
                "Error: InitializeSteam failed",
                Path.Combine(root, "authentication.log")));
            AssertContains(authenticationMessage, "Steam sign-in failed", "authentication failure");

            var passwordMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                1,
                "Logging 'player' into Steam3...\nUnable to login to Steam3: InvalidPassword\nError: InitializeSteam failed",
                Path.Combine(root, "password.log")));
            AssertContains(passwordMessage, "Steam sign-in failed", "rejected password");

            // A Steam Guard login and a depot key both succeed before the disk fills up.
            var diskMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                1,
                "Logging 'player' into Steam3...\nThis account is protected by Steam Guard.\n" +
                "Please enter your 2 factor auth code from your authenticator app:  Done!\n" +
                "Got depot key for 774792 result: OK\n" +
                "Got manifest request code for depot 774792 from app 774791, manifest 1, result: 42\n" +
                "Failed to allocate file C:\\Games\\SS2\\data.bin: There is not enough space on the disk.",
                Path.Combine(root, "disk.log")));
            AssertContains(diskMessage, "could not write the game files", "disk full after a Steam Guard login");

            // A connection retry that recovered must not outrank the manifest refusal that ended the run.
            var manifestMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                1,
                "Connection to Steam failed. Trying again (#1)...\nLogging 'player' into Steam3... Done!\n" +
                "Got depot key for 774792 result: OK\nEncountered 401 for depot manifest 774792 1. Aborting.",
                Path.Combine(root, "manifest.log")));
            AssertContains(manifestMessage, "denied access to the required depot", "manifest refused after a retry");

            var networkMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                1,
                "Connection to Steam failed. Trying again (#1)...\nCould not connect to Steam after 10 tries\n" +
                "Error: InitializeSteam failed",
                Path.Combine(root, "network.log")));
            AssertContains(networkMessage, "could not reliably connect", "no connection to Steam");

            var interruptedMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                StatusControlCExit,
                "Logging 'player' into Steam3...",
                Path.Combine(root, "interrupted.log")));
            AssertContains(interruptedMessage, "interrupted", "Ctrl+C");

            var unknownMessage = BuildDepotDownloaderFailureMessage(new DepotDownloaderResult(
                23,
                "A future DepotDownloader error that Setup has never seen",
                Path.Combine(root, "unknown.log")));
            AssertContains(unknownMessage, "could not classify automatically", "unknown failure");
            AssertContains(unknownMessage, "Exit code: 23", "unknown exit code");
            AssertContains(unknownMessage, "future DepotDownloader error", "unknown diagnostic details");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void AssertContains(string value, string expected, string scenario)
    {
        if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Self-test failed for {scenario}: expected '{expected}'.");
    }

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private sealed class RollingTextBuffer(int maximumCharacters)
    {
        private readonly StringBuilder _value = new();

        // Trimmed only once it doubles, so a long download does not shift the whole buffer per chunk.
        internal void Append(ReadOnlySpan<char> value)
        {
            _value.Append(value);
            if (_value.Length > maximumCharacters * 2)
                _value.Remove(0, _value.Length - maximumCharacters);
        }

        public override string ToString() =>
            _value.Length <= maximumCharacters
                ? _value.ToString()
                : _value.ToString(_value.Length - maximumCharacters, maximumCharacters);
    }

    /// <summary>
    /// The console DepotDownloader signs in through. Setup is attached to it as well, which means
    /// Windows sends the console's Ctrl+C and close events to Setup too, and by default either one
    /// ends Setup on the spot with no message. Ctrl+C is swallowed for Setup - DepotDownloader
    /// still receives it and exits, and Setup reports that - and the window's close button is
    /// removed so that Cancel in Setup is the way out. Windows Terminal draws its own tab close
    /// button that this cannot reach.
    /// </summary>
    private sealed class DepotConsoleSession : IDisposable
    {
        private const uint CtrlCEvent = 0;
        private const uint CtrlBreakEvent = 1;
        private const uint ScClose = 0xF060;
        private const uint MfByCommand = 0;
        private const uint Utf8CodePage = 65001;

        // Static so the delegate outlives every registration Windows holds a pointer to.
        private static readonly ConsoleCtrlHandler IgnoreInterrupt = signal => signal is CtrlCEvent or CtrlBreakEvent;

        private readonly bool _ownsConsole;
        private readonly bool _handlerInstalled;
        private readonly TextReader _originalInput;
        private readonly TextWriter _originalOutput;
        private readonly TextWriter _originalError;
        private readonly StreamReader _input;

        private DepotConsoleSession(bool ownsConsole, bool handlerInstalled, Encoding? childOutputEncoding,
                                    StreamReader input, StreamWriter output)
        {
            _ownsConsole = ownsConsole;
            _handlerInstalled = handlerInstalled;
            ChildOutputEncoding = childOutputEncoding;
            _originalInput = Console.In;
            _originalOutput = Console.Out;
            _originalError = Console.Error;
            _input = input;
            Output = output;
            Error = output;
            Console.SetIn(input);
            Console.SetOut(output);
            Console.SetError(output);
        }

        internal TextWriter Output { get; }
        internal TextWriter Error { get; }

        /// <summary>UTF-8 when Setup switched its own console to it; otherwise null, the default.</summary>
        internal Encoding? ChildOutputEncoding { get; }

        internal static DepotConsoleSession Open()
        {
            var ownsConsole = GetConsoleWindow() == IntPtr.Zero;
            if (ownsConsole && !AllocConsole())
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Setup could not open the interactive Steam sign-in window.");

            var handlerInstalled = false;
            try
            {
                SetConsoleTitle("SS2 Revive Setup - Steam sign-in");
                handlerInstalled = SetConsoleCtrlHandler(IgnoreInterrupt, true);

                Encoding? childOutputEncoding = null;
                if (ownsConsole)
                {
                    var menu = GetSystemMenu(GetConsoleWindow(), false);
                    if (menu != IntPtr.Zero) DeleteMenu(menu, ScClose, MfByCommand);

                    // Paths and messages outside ASCII would otherwise be mangled by the OEM code page.
                    if (SetConsoleOutputCP(Utf8CodePage)) childOutputEncoding = new UTF8Encoding(false);
                }

                var inputStream = new FileStream("CONIN$", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var outputStream = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                var input = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                var output = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
                return new DepotConsoleSession(ownsConsole, handlerInstalled, childOutputEncoding, input, output);
            }
            catch
            {
                if (handlerInstalled) SetConsoleCtrlHandler(IgnoreInterrupt, false);
                if (ownsConsole) FreeConsole();
                throw;
            }
        }

        public void Dispose()
        {
            Console.SetIn(_originalInput);
            Console.SetOut(_originalOutput);
            Console.SetError(_originalError);
            _input.Dispose();
            Output.Dispose();
            if (_handlerInstalled) SetConsoleCtrlHandler(IgnoreInterrupt, false);
            if (_ownsConsole) FreeConsole();
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool ConsoleCtrlHandler(uint signal);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler,
                                                         [MarshalAs(UnmanagedType.Bool)] bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleOutputCP(uint codePage);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleTitle(string title);
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
