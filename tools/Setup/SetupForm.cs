using System.Diagnostics;

namespace SS2Revive.Setup;

internal sealed class SetupForm : Form
{
    private readonly Panel _startPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _progressPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _completePage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox _installPath = new();
    private readonly TextBox _steamUsername = new();
    private readonly Label _progressStatus = new();
    private readonly TextBox _progressLog = new();
    private readonly Label _completionTitle = new();
    private readonly Label _completionSummary = new();
    private readonly Label _completionInstructions = new();
    private readonly Button _installButton = new();
    private readonly Button _launchButton = new();
    private readonly Button _openFolderButton = new();
    private CancellationTokenSource? _cancellation;
    private InstallerEngine.InstallResult? _result;
    private bool _installing;
    private bool _closeAfterCancellation;

    internal SetupForm()
    {
        Text = "SS2 Revive Setup";
        ClientSize = new Size(720, 470);
        MinimumSize = new Size(720, 470);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        BuildStartPage();
        BuildProgressPage();
        BuildCompletePage();
        Controls.Add(_completePage);
        Controls.Add(_progressPage);
        Controls.Add(_startPage);
        FormClosing += OnFormClosing;
    }

    private void BuildStartPage()
    {
        var title = TitleLabel("Install SS2 Revive");
        title.Location = new Point(32, 24);
        _startPage.Controls.Add(title);

        var intro = new Label
        {
            AutoSize = false,
            Location = new Point(35, 72),
            Size = new Size(650, 52),
            Text = "Creates a separate Surgeon Simulator 2 build 1.3.7 installation, then adds x64 BepInEx and the latest published SS2 Revive release.",
        };
        _startPage.Controls.Add(intro);

        var installLabel = FieldLabel("Install location", 136);
        _startPage.Controls.Add(installLabel);
        _installPath.Location = new Point(35, 164);
        _installPath.Size = new Size(544, 30);
        _installPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SS2Revive",
            InstallerEngine.BuildFolderName);
        _startPage.Controls.Add(_installPath);

        var browse = new Button
        {
            Text = "Browse…",
            Location = new Point(590, 162),
            Size = new Size(95, 34),
        };
        browse.Click += (_, _) => BrowseForInstallFolder();
        _startPage.Controls.Add(browse);

        var steamLabel = FieldLabel("Steam login name", 216);
        _startPage.Controls.Add(steamLabel);
        _steamUsername.Location = new Point(35, 244);
        _steamUsername.Size = new Size(650, 30);
        _startPage.Controls.Add(_steamUsername);

        var credentialNote = new Label
        {
            AutoSize = false,
            Location = new Point(35, 284),
            Size = new Size(650, 68),
            ForeColor = Color.FromArgb(80, 80, 80),
            Text = "Steam must own Surgeon Simulator 2. DepotDownloader opens a separate sign-in window for your password and Steam Guard code. SS2 Revive Setup never reads or stores those credentials.",
        };
        _startPage.Controls.Add(credentialNote);

        _installButton.Text = "Install";
        _installButton.Location = new Point(545, 391);
        _installButton.Size = new Size(140, 42);
        _installButton.Click += async (_, _) => await BeginInstallAsync();
        _startPage.Controls.Add(_installButton);

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(393, 391),
            Size = new Size(140, 42),
        };
        cancel.Click += (_, _) => Close();
        _startPage.Controls.Add(cancel);
        AcceptButton = _installButton;
        CancelButton = cancel;
    }

    private void BuildProgressPage()
    {
        var title = TitleLabel("Installing");
        title.Location = new Point(32, 24);
        _progressPage.Controls.Add(title);

        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Location = new Point(35, 80),
            Size = new Size(650, 22),
        };
        _progressPage.Controls.Add(bar);

        _progressStatus.Location = new Point(35, 117);
        _progressStatus.Size = new Size(650, 48);
        _progressStatus.Text = "Preparing setup…";
        _progressPage.Controls.Add(_progressStatus);

        _progressLog.Location = new Point(35, 170);
        _progressLog.Size = new Size(650, 205);
        _progressLog.Multiline = true;
        _progressLog.ReadOnly = true;
        _progressLog.ScrollBars = ScrollBars.Vertical;
        _progressLog.BackColor = SystemColors.Window;
        _progressPage.Controls.Add(_progressLog);

        var note = new Label
        {
            AutoSize = false,
            Location = new Point(35, 388),
            Size = new Size(500, 50),
            ForeColor = Color.FromArgb(80, 80, 80),
            Text = "Do not close Setup or the DepotDownloader sign-in window while installation is running.",
        };
        _progressPage.Controls.Add(note);

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(545, 391),
            Size = new Size(140, 42),
        };
        cancel.Click += (_, _) => _cancellation?.Cancel();
        _progressPage.Controls.Add(cancel);
    }

    private void BuildCompletePage()
    {
        _completionTitle.Text = "Installation complete";
        _completionTitle.AutoSize = true;
        _completionTitle.Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold);
        _completionTitle.Location = new Point(32, 24);
        _completePage.Controls.Add(_completionTitle);

        _completionSummary.AutoSize = false;
        _completionSummary.Location = new Point(35, 82);
        _completionSummary.Size = new Size(650, 235);
        _completionSummary.Font = new Font(Font.FontFamily, 10.5f);
        _completePage.Controls.Add(_completionSummary);

        _completionInstructions.AutoSize = false;
        _completionInstructions.Location = new Point(35, 322);
        _completionInstructions.Size = new Size(650, 56);
        _completePage.Controls.Add(_completionInstructions);

        _openFolderButton.Text = "Open folder";
        _openFolderButton.Location = new Point(241, 391);
        _openFolderButton.Size = new Size(140, 42);
        _openFolderButton.Click += (_, _) => OpenPath(_result?.InstallDirectory);
        _completePage.Controls.Add(_openFolderButton);

        _launchButton.Text = "Launch game";
        _launchButton.Location = new Point(393, 391);
        _launchButton.Size = new Size(140, 42);
        _launchButton.Click += (_, _) => OpenPath(_result?.LauncherPath);
        _completePage.Controls.Add(_launchButton);

        var finish = new Button
        {
            Text = "Finish",
            Location = new Point(545, 391),
            Size = new Size(140, 42),
        };
        finish.Click += (_, _) => Close();
        _completePage.Controls.Add(finish);
    }

    private async Task BeginInstallAsync()
    {
        var path = _installPath.Text.Trim().Trim('"');
        var username = _steamUsername.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(this, "Choose an install location and enter your Steam login name.",
                "Missing information", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { path = Path.GetFullPath(path); }
        catch (Exception exception)
        {
            MessageBox.Show(this, "The install path is invalid: " + exception.Message,
                "Invalid path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installing = true;
        _cancellation = new CancellationTokenSource();
        _startPage.Visible = false;
        _progressPage.Visible = true;
        _progressPage.BringToFront();
        var progress = new Progress<string>(message =>
        {
            _progressStatus.Text = message;
            _progressLog.AppendText(message + Environment.NewLine);
        });

        try
        {
            _result = await InstallerEngine.InstallAsync(path, username, progress, _cancellation.Token);
            ShowCompletion(_result);
        }
        catch (OperationCanceledException)
        {
            if (!_closeAfterCancellation)
            {
                MessageBox.Show(this, "Installation was cancelled. You can run Setup again to resume and validate the download.",
                    "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _progressPage.Visible = false;
                _startPage.Visible = true;
                _startPage.BringToFront();
            }
        }
        catch (Exception exception)
        {
            if (!_closeAfterCancellation)
            {
                MessageBox.Show(this, exception.Message, "Installation failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _progressPage.Visible = false;
                _startPage.Visible = true;
                _startPage.BringToFront();
            }
        }
        finally
        {
            _installing = false;
            _cancellation.Dispose();
            _cancellation = null;
            if (_closeAfterCancellation && !IsDisposed) Close();
        }
    }

    private void ShowCompletion(InstallerEngine.InstallResult result)
    {
        _completionTitle.Text = result.ModInstalled
            ? "Installation complete"
            : "Setup incomplete";
        _completionSummary.Text =
            $"Installed to:\r\n{result.InstallDirectory}\r\n\r\n" +
            $"Launcher:\r\n{result.LauncherPath}\r\n\r\n" +
            (result.ModInstalled
                ? $"SS2 Revive mod: Installed successfully ({result.ModVersion})"
                : $"SS2 Revive mod: Not installed automatically\r\n{result.ModError}");
        _completionInstructions.Text = result.ModInstalled
            ? "Keep Steam running and signed in. Use the generated launcher—not the normal Steam Play button—to start build 1.3.7."
            : "The game and BepInEx are ready, but the mod still needs to be installed. Open the folder and follow the manual installation steps in the SS2 Revive README, then use the generated launcher.";
        _launchButton.Enabled = result.ModInstalled && File.Exists(result.LauncherPath);
        _openFolderButton.Enabled = Directory.Exists(result.InstallDirectory);
        _progressPage.Visible = false;
        _completePage.Visible = true;
        _completePage.BringToFront();
    }

    private void BrowseForInstallFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose where the separate Surgeon Simulator 2 build 1.3.7 copy will be installed",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_installPath.Text) ? _installPath.Text : string.Empty,
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            _installPath.Text = Path.Combine(picker.SelectedPath, InstallerEngine.BuildFolderName);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_installing) return;
        if (_closeAfterCancellation)
        {
            eventArgs.Cancel = true;
            return;
        }
        var result = MessageBox.Show(this, "Cancel the installation and close Setup?",
            "Installation running", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            eventArgs.Cancel = true;
            return;
        }
        _closeAfterCancellation = true;
        _progressStatus.Text = "Cancelling installation…";
        _cancellation?.Cancel();
        eventArgs.Cancel = true;
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show("Could not open the requested path: " + exception.Message,
                "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label TitleLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
    };

    private static Label FieldLabel(string text, int top) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(35, top),
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
    };
}
