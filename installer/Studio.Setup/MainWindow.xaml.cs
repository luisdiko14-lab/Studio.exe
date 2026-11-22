using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace StudioSetup;

public partial class MainWindow : Window
{
    private const string ReleasesApi =
        "https://api.github.com/repos/luisdiko14-lab/Studio.exe/releases/latest";

    private readonly HttpClient _http = new();

    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Studio.exe");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Studio-Setup/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose the parent folder for Studio.exe",
            InitialDirectory = Directory.Exists(InstallPathBox.Text)
                ? InstallPathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Multiselect = false
        };

        if (picker.ShowDialog(this) == true)
            InstallPathBox.Text = Path.Combine(picker.FolderName, "Studio.exe");
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var installDirectory = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            MessageBox.Show(this, "Choose an installation folder first.",
                "Studio.exe Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        string workingDirectory = Path.Combine(Path.GetTempPath(),
            "StudioSetup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingDirectory);
            SetProgress(2, "Checking the latest GitHub release…");

            var (downloadUrl, checksumUrl, packageName, releaseName) = await FindLatestPackageAsync();
            SetProgress(5, $"Downloading {releaseName}…");

            string zipPath = Path.Combine(workingDirectory, "Studio-win-x64.zip");
            await DownloadPackageAsync(downloadUrl, zipPath);
            SetProgress(76, "Verifying the download checksum…");
            string checksums = await _http.GetStringAsync(checksumUrl);
            string? checksumLine = checksums.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.EndsWith("  " + packageName, StringComparison.OrdinalIgnoreCase));
            if (checksumLine is null)
                throw new InvalidDataException("The release checksum file does not contain the ZIP checksum.");
            string expectedHash = checksumLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            await using (var verifyStream = File.OpenRead(zipPath))
            {
                string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream)).ToLowerInvariant();
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded ZIP failed SHA-256 verification. The installer stopped to protect your files.");
            }

            string extractedDirectory = Path.Combine(workingDirectory, "extracted");
            Directory.CreateDirectory(extractedDirectory);
            await ExtractPackageAsync(zipPath, extractedDirectory);

            string appExe = Path.Combine(extractedDirectory, "Studio.exe");
            string monacoLoader = Path.Combine(extractedDirectory,
                "Assets", "monaco", "vs", "loader.js");
            if (!File.Exists(appExe) || !File.Exists(monacoLoader))
                throw new InvalidDataException(
                    "The downloaded package is incomplete. Studio.exe or its bundled Monaco assets are missing.");

            SetProgress(88, "Copying Studio.exe into the selected folder…");
            CopyDirectory(extractedDirectory, installDirectory);
            string installedExe = Path.Combine(installDirectory, "Studio.exe");
            if (!File.Exists(installedExe))
                throw new IOException("Studio.exe was not found after extraction.");

            SetProgress(96, "Creating shortcuts…");
            if (StartMenuCheckBox.IsChecked == true)
            {
                string startMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", "Studio.exe");
                CreateShortcut(Path.Combine(startMenu, "Studio.exe.lnk"),
                    installedExe, installDirectory);
            }

            if (DesktopCheckBox.IsChecked == true)
            {
                string desktop = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktop, "Studio.exe.lnk"),
                    installedExe, installDirectory);
            }

            SetProgress(100, $"Installed successfully from {releaseName}.");
            InstallButton.Content = "Installed";
            MessageBox.Show(this,
                "Studio.exe has been installed successfully.",
                "Installation complete", MessageBoxButton.OK, MessageBoxImage.Information);

            if (LaunchCheckBox.IsChecked == true)
            {
                Process.Start(new ProcessStartInfo(installedExe)
                {
                    WorkingDirectory = installDirectory,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            SetProgress(0, "Installation failed.");
            MessageBox.Show(this, ex.Message, "Studio.exe Setup",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, true); }
            catch { /* Temporary files can be removed on the next run if still locked. */ }
            SetBusy(false);
        }
    }

    private async Task<(string Url, string ChecksumUrl, string PackageName, string Name)> FindLatestPackageAsync()
    {
        using var response = await _http.GetAsync(ReleasesApi);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        var root = json.RootElement;
        string releaseName = root.TryGetProperty("tag_name", out var tag)
            ? tag.GetString() ?? "latest"
            : "latest";

        if (!root.TryGetProperty("assets", out var assets))
            throw new InvalidDataException("The latest GitHub release has no downloadable assets.");

        string? packageUrl = null;
        string? packageName = null;
        string? checksumUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (name.Equals("Studio-win-x64.zip", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Studio.exe-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                packageUrl = url;
                packageName = name;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
        }

        if (!string.IsNullOrWhiteSpace(packageUrl) &&
            !string.IsNullOrWhiteSpace(packageName) &&
            !string.IsNullOrWhiteSpace(checksumUrl))
            return (packageUrl, checksumUrl, packageName, releaseName);

        throw new FileNotFoundException(
            "The latest GitHub release must contain Studio-win-x64.zip and SHA256SUMS.txt. Please try again after the release workflow finishes.");
    }

    private async Task DownloadPackageAsync(string url, string destination)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1;
        long received = 0;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destination, FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            if (totalBytes > 0)
            {
                double fraction = Math.Clamp((double)received / totalBytes, 0, 1);
                SetProgress(5 + fraction * 70,
                    $"Downloading… {received / 1048576d:0.0} / {totalBytes / 1048576d:0.0} MB");
            }
            else
            {
                SetProgress(5, $"Downloading… {received / 1048576d:0.0} MB");
            }
        }
    }

    private async Task ExtractPackageAsync(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        int total = Math.Max(archive.Entries.Count, 1);
        int completed = 0;
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release ZIP contains an unsafe file path.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            completed++;
            SetProgress(75 + (completed * 12d / total), "Extracting application files…");
        }

        await Task.CompletedTask;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.GetFiles(source, "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath,
        string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            throw new InvalidOperationException("Windows Script Host is unavailable; shortcuts could not be created.");

        object? shellObject = Activator.CreateInstance(shellType);
        if (shellObject is null)
            throw new InvalidOperationException("Could not create the Windows shortcut service.");

        try
        {
            dynamic shell = shellObject;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = "Studio.exe development environment";
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Save();
            if (Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            if (Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }
    }

    private void SetProgress(double value, string status)
    {
        InstallProgress.Value = value;
        PercentText.Text = $"{value:0}%";
        StatusText.Text = status;
    }

    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        InstallPathBox.IsEnabled = !busy;
        StartMenuCheckBox.IsEnabled = !busy;
        DesktopCheckBox.IsEnabled = !busy;
        LaunchCheckBox.IsEnabled = !busy;
        if (busy)
            InstallButton.Content = "Installing…";
        else if (InstallProgress.Value < 100)
            InstallButton.Content = "Install Studio.exe";
    }
}
