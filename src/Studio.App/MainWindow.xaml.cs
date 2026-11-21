using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using StudioApp.Models;
using StudioApp.Services;

namespace StudioApp;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly TerminalSession _terminal = new();
    private readonly List<ExplorerEntry> _explorerEntries = [];
    private readonly List<string> _sceneNames = [];
    private readonly List<SceneObject> _scene = [];
    private AppSettings _settings;
    private string? _projectRoot, _currentDirectory, _currentFile;
    private bool _isLoadingEditor, _isDirty, _terminalVisible = true, _monacoReady;
    private string _monacoText = string.Empty;
    private Process? _runProcess;
    private Model3DGroup _world = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        ExplorerList.ItemsSource = _explorerEntries;
        SceneList.ItemsSource = _sceneNames;
        WizardRoot.Visibility = _settings.SetupCompleted ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceRoot.Visibility = _settings.SetupCompleted ? Visibility.Visible : Visibility.Collapsed;
        StudioNameInput.Text = _settings.StudioName;
        StudioNamePreview.Text = _settings.StudioName;
        DefaultFolderInput.Text = _settings.DefaultProjectDirectory;
        ApplyBranding();
        RefreshExtensionStatus();
        _terminal.OutputReceived += AppendTerminal;
        if (_settings.SetupCompleted) InitializeViewport();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.SetupCompleted) { StartTerminal("PowerShell"); _ = InitializeMonacoAsync(); }
        else StudioNameInput.Focus();
    }

    private void ApplyBranding()
    {
        Title = string.IsNullOrWhiteSpace(_settings.StudioName) ? "Studio.exe" : _settings.StudioName;
        BrandText.Text = Title;
        StudioNamePreview.Text = Title;
        var accent = _settings.AccentColor switch
        {
            "#4D9BFF" => Color.FromRgb(77, 155, 255),
            "#A783FF" => Color.FromRgb(167, 131, 255),
            "#35B6A2" => Color.FromRgb(53, 182, 162),
            _ => Color.FromRgb(115, 135, 255)
        };
        if (Application.Current.Resources["AccentBrush"] is SolidColorBrush brush) brush.Color = accent;
        if (_settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current.Resources["WindowBrush"] = new SolidColorBrush(Color.FromRgb(242, 245, 250));
            Application.Current.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(230, 235, 243));
            StatusText.Text = "Light theme selected; editor surfaces retain the dark coding palette.";
        }
        else
        {
            Application.Current.Resources["WindowBrush"] = new SolidColorBrush(Color.FromRgb(23, 26, 33));
            Application.Current.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(32, 36, 46));
        }
    }

    private void WizardStart_Click(object sender, RoutedEventArgs e)
    {
        WelcomeStep.Visibility = Visibility.Collapsed; NameStep.Visibility = Visibility.Visible;
        StudioNameInput.Focus(); StudioNameInput.SelectAll();
    }
    private void WizardBackToWelcome_Click(object sender, RoutedEventArgs e)
    {
        NameStep.Visibility = Visibility.Collapsed; WelcomeStep.Visibility = Visibility.Visible;
    }
    private void StudioNameInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StudioNamePreview is not null)
            StudioNamePreview.Text = string.IsNullOrWhiteSpace(StudioNameInput.Text) ? "Your Studio" : StudioNameInput.Text.Trim();
    }
    private void WizardNameNext_Click(object sender, RoutedEventArgs e)
    {
        var name = StudioNameInput.Text.Trim();
        if (name.Length is < 2 or > 40)
        {
            MessageBox.Show("Choose a studio name between 2 and 40 characters.", "Studio name");
            return;
        }
        NameStep.Visibility = Visibility.Collapsed; PersonalizeStep.Visibility = Visibility.Visible;
        DefaultFolderInput.Text = string.IsNullOrWhiteSpace(_settings.DefaultProjectDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : _settings.DefaultProjectDirectory;
    }
    private void WizardBackToName_Click(object sender, RoutedEventArgs e)
    {
        PersonalizeStep.Visibility = Visibility.Collapsed; NameStep.Visibility = Visibility.Visible;
    }
    private void ChooseDefaultFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your default projects folder",
            InitialDirectory = Directory.Exists(DefaultFolderInput.Text) ? DefaultFolderInput.Text :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() == true) DefaultFolderInput.Text = dialog.FolderName;
    }
    private void FinishSetup_Click(object sender, RoutedEventArgs e)
    {
        _settings.StudioName = StudioNameInput.Text.Trim();
        _settings.Theme = (ThemePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dark";
        _settings.AccentColor = (AccentPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "Blue" => "#4D9BFF", "Violet" => "#A783FF", "Teal" => "#35B6A2", _ => "#7387FF"
        };
        _settings.DefaultProjectDirectory = Directory.Exists(DefaultFolderInput.Text) ? DefaultFolderInput.Text :
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _settings.SetupCompleted = true;
        PersistSettings(); ApplyBranding();
        WizardRoot.Visibility = Visibility.Collapsed; WorkspaceRoot.Visibility = Visibility.Visible;
        InitializeViewport(); StartTerminal("PowerShell"); _ = InitializeMonacoAsync(); StatusText.Text = "Studio ready";
    }
    private void PersistSettings()
    {
        try { _settingsService.Save(_settings); }
        catch (Exception ex) { MessageBox.Show($"Could not save Studio settings:\n{ex.Message}", "Settings error"); }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open a project folder",
            InitialDirectory = Directory.Exists(_settings.DefaultProjectDirectory) ? _settings.DefaultProjectDirectory :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() != true) return;
        _projectRoot = dialog.FolderName; _currentDirectory = _projectRoot;
        ProjectStatusText.Text = _projectRoot; RefreshExplorer(); RestartTerminal("PowerShell");
        StatusText.Text = "Project opened";
    }

    private void RefreshExplorer()
    {
        _explorerEntries.Clear();
        if (string.IsNullOrWhiteSpace(_currentDirectory) || !Directory.Exists(_currentDirectory)) return;
        try
        {
            if (!string.Equals(_currentDirectory, _projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(_currentDirectory);
                if (parent is not null) _explorerEntries.Add(new ExplorerEntry("..", parent.FullName, true));
            }
            foreach (var directory in Directory.EnumerateDirectories(_currentDirectory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                _explorerEntries.Add(new ExplorerEntry(Path.GetFileName(directory), directory, true));
            foreach (var file in Directory.EnumerateFiles(_currentDirectory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                _explorerEntries.Add(new ExplorerEntry(Path.GetFileName(file), file, false));
        }
        catch (UnauthorizedAccessException) { StatusText.Text = "This folder cannot be read with current permissions."; }
        catch (Exception ex) { StatusText.Text = $"Explorer error: {ex.Message}"; }
        ExplorerList.ItemsSource = null;
        ExplorerList.ItemsSource = _explorerEntries;
    }
    private void RefreshExplorer_Click(object sender, RoutedEventArgs e) => RefreshExplorer();
    private void ExplorerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExplorerList.SelectedItem is not ExplorerEntry entry) return;
        if (entry.IsDirectory) { _currentDirectory = entry.FullPath; RefreshExplorer(); }
        else OpenFile(entry.FullPath);
    }
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open source file",
            InitialDirectory = Directory.Exists(_currentDirectory) ? _currentDirectory :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true) OpenFile(dialog.FileName);
    }
    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create a new file",
            InitialDirectory = Directory.Exists(_currentDirectory) ? _currentDirectory : _settings.DefaultProjectDirectory,
            FileName = "new-file.js", Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                if (!File.Exists(dialog.FileName)) File.WriteAllText(dialog.FileName, string.Empty);
                OpenFile(dialog.FileName); RefreshExplorer();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Create file failed"); }
        }
    }
    private void OpenFile(string path)
    {
        if (!ConfirmDiscardOrSave()) return;
        try
        {
            _isLoadingEditor = true; EditorText.Text = File.ReadAllText(path);
            _monacoText = EditorText.Text;
            _currentFile = path;
            _ = SetMonacoTextAsync(_monacoText, path); _isDirty = false; CurrentFileText.Text = Path.GetFileName(path);
            StatusText.Text = $"Opened {Path.GetFileName(path)}"; WorkspaceTabs.SelectedItem = EditorTab;
        }
        catch (Exception ex) { MessageBox.Show($"Could not open file:\n{ex.Message}", "Open file failed"); }
        finally { _isLoadingEditor = false; }
    }
    private void EditorText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEditor) return;
        _isDirty = _currentFile is not null;
        if (_isDirty) CurrentFileText.Text = $"{Path.GetFileName(_currentFile)} •";
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrentFile();
    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            InitialDirectory = Directory.Exists(_currentDirectory) ? _currentDirectory : "",
            FileName = _currentFile is null ? "new-file.txt" : Path.GetFileName(_currentFile)
        };
        if (dialog.ShowDialog() == true) { _currentFile = dialog.FileName; SaveCurrentFile(); }
    }
    private bool SaveCurrentFile()
    {
        if (_currentFile is null) { SaveAs_Click(this, new RoutedEventArgs()); return !_isDirty; }
        try
        {
            var content = WorkspaceTabs.SelectedItem == MonacoTab ? _monacoText : EditorText.Text;
            File.WriteAllText(_currentFile, content, new UTF8Encoding(false));
            _isDirty = false; CurrentFileText.Text = Path.GetFileName(_currentFile);
            StatusText.Text = "File saved"; RefreshExplorer(); return true;
        }
        catch (Exception ex) { MessageBox.Show($"Could not save file:\n{ex.Message}", "Save failed"); return false; }
    }
    private bool ConfirmDiscardOrSave()
    {
        if (!_isDirty) return true;
        var answer = MessageBox.Show("Save changes to the current file before continuing?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveCurrentFile();
        return true;
    }

    private void StartTerminal(string shell)
    {
        try
        {
            var directory = Directory.Exists(_projectRoot) ? _projectRoot! :
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _terminal.Start(directory, shell); StatusText.Text = $"{shell} session started";
        }
        catch (Exception ex) { AppendTerminal($"Could not start {shell}: {ex.Message}"); }
    }
    private void RestartTerminal(string shell) { _terminal.Stop(); StartTerminal(shell); }
    private void AppendTerminal(string line)
    {
        Dispatcher.BeginInvoke(new Action(() => { TerminalOutput.AppendText(line + Environment.NewLine); TerminalOutput.ScrollToEnd(); }));
    }
    private void SendTerminalCommand_Click(object sender, RoutedEventArgs e) => SendTerminalCommand();
    private void TerminalCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendTerminalCommand(); e.Handled = true; }
    }
    private void SendTerminalCommand()
    {
        var command = TerminalCommand.Text.Trim();
        if (command.Length == 0) return;
        AppendTerminal($"> {command}");
        try { if (!_terminal.IsRunning) StartTerminal("PowerShell"); _terminal.WriteLine(command); }
        catch (Exception ex) { AppendTerminal($"Terminal error: {ex.Message}"); }
        TerminalCommand.Clear();
    }
    private void PowerShell_Click(object sender, RoutedEventArgs e) => RestartTerminal("PowerShell");
    private void CommandPrompt_Click(object sender, RoutedEventArgs e) => RestartTerminal("Command Prompt");
    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => TerminalOutput.Clear();
    private void ToggleTerminal_Click(object sender, RoutedEventArgs e)
    {
        _terminalVisible = !_terminalVisible;
        TerminalPanel.Visibility = _terminalVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null)
        {
            MessageBox.Show("Open a JavaScript, Python, or C# project file first.", "Nothing to run");
            return;
        }
        if (_isDirty && !SaveCurrentFile()) return;
        var ext = Path.GetExtension(_currentFile).ToLowerInvariant();
        var info = new ProcessStartInfo
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Directory.Exists(_projectRoot) ? _projectRoot! : Path.GetDirectoryName(_currentFile)!
        };
        switch (ext)
        {
            case ".js":
            case ".mjs": info.FileName = "node"; info.ArgumentList.Add(_currentFile); break;
            case ".py": info.FileName = "python"; info.ArgumentList.Add(_currentFile); break;
            case ".csproj":
                info.FileName = "dotnet"; info.ArgumentList.Add("run"); info.ArgumentList.Add("--project"); info.ArgumentList.Add(_currentFile); break;
            case ".cs":
                MessageBox.Show("Run a C# project (.csproj), not a standalone .cs file. Create a Console App with the .NET SDK, then open its project file.", "C# project required"); return;
            default:
                MessageBox.Show("This build runs .js, .mjs, .py, and .csproj files.", "Unsupported file"); return;
        }
        StartToolProcess(info, "Run");
    }
    private async void CheckSyntax_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null) { MessageBox.Show("Open a source file before checking it.", "No file selected"); return; }
        if (_isDirty && !SaveCurrentFile()) return;
        var extension = Path.GetExtension(_currentFile).ToLowerInvariant();
        var info = new ProcessStartInfo
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_currentFile)!
        };
        if (extension is ".js" or ".mjs" or ".cjs")
        {
            info.FileName = "node"; info.ArgumentList.Add("--check"); info.ArgumentList.Add(_currentFile);
        }
        else if (extension == ".py")
        {
            info.FileName = "python"; info.ArgumentList.Add("-m"); info.ArgumentList.Add("py_compile"); info.ArgumentList.Add(_currentFile);
        }
        else if (extension == ".csproj")
        {
            info.FileName = "dotnet"; info.ArgumentList.Add("build"); info.ArgumentList.Add(_currentFile);
        }
        else if (extension == ".cs")
        {
            var root = _projectRoot ?? Path.GetDirectoryName(_currentFile)!;
            var project = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                  && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            if (project is null) { MessageBox.Show("Open a C# project folder containing a .csproj to check C# diagnostics.", "Project required"); return; }
            info.FileName = "dotnet"; info.ArgumentList.Add("build"); info.ArgumentList.Add(project);
            info.WorkingDirectory = Path.GetDirectoryName(project)!;
        }
        else
        {
            MessageBox.Show("Syntax diagnostics support JavaScript, Python, and C# project files.", "Unsupported file");
            return;
        }

        try
        {
            StatusText.Text = "Checking source…";
            AppendTerminal($"[Diagnostics] {info.FileName} {string.Join(" ", info.ArgumentList)}");
            using var process = new Process { StartInfo = info };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = output + Environment.NewLine + error;
            foreach (var line in combined.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                AppendTerminal(line);

            var diagnostics = new List<EditorDiagnostic>();
            if (extension is ".csproj" or ".cs")
            {
                var pattern = new Regex(@"(?<file>.+?)\((?<line>\d+),(?<column>\d+)\): (?<level>error|warning) (?<code>[A-Za-z]+\d+): (?<message>.+)", RegexOptions.IgnoreCase);
                foreach (Match match in pattern.Matches(combined))
                {
                    if (!Path.GetFileName(match.Groups["file"].Value).Equals(Path.GetFileName(_currentFile), StringComparison.OrdinalIgnoreCase)) continue;
                    diagnostics.Add(new EditorDiagnostic(
                        int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture),
                        match.Groups["message"].Value,
                        match.Groups["level"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning"));
                }
            }
            else if (extension == ".py")
            {
                var match = Regex.Match(combined, @"File ""[^""]+"", line (?<line>\d+)");
                if (match.Success && process.ExitCode != 0)
                {
                    var message = combined.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Python syntax error";
                    diagnostics.Add(new EditorDiagnostic(int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture), 1, message, "Error"));
                }
            }
            else if (process.ExitCode != 0)
            {
                var fileLine = Regex.Match(combined, Regex.Escape(_currentFile) + @":(?<line>\d+)");
                var line = fileLine.Success ? int.Parse(fileLine.Groups["line"].Value, CultureInfo.InvariantCulture) : 1;
                var message = combined.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "JavaScript syntax error";
                diagnostics.Add(new EditorDiagnostic(line, 1, message, "Error"));
            }

            await SetMonacoDiagnosticsAsync(diagnostics);
            var errors = diagnostics.Count(x => x.Severity == "Error");
            var warnings = diagnostics.Count - errors;
            StatusText.Text = process.ExitCode == 0 ? $"Check passed · {warnings} warning(s)" : $"Check failed · {errors} error(s), {warnings} warning(s)";
            AppendTerminal($"[Diagnostics complete: exit {process.ExitCode}, {errors} error(s), {warnings} warning(s)]");
            WorkspaceTabs.SelectedItem = MonacoTab;
        }
        catch (Exception ex)
        {
            AppendTerminal($"Diagnostics failed: {ex.Message}");
            StatusText.Text = "Diagnostics failed";
        }
    }

    private async Task SetMonacoDiagnosticsAsync(List<EditorDiagnostic> diagnostics)
    {
        if (!_monacoReady) return;
        var json = JsonSerializer.Serialize(diagnostics.Select(d => new
        {
            line = d.Line, column = d.Column, message = d.Message, severity = d.Severity
        }));
        await MonacoEditor.ExecuteScriptAsync("window.studioSetDiagnostics(" + json + ");");
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null)
        {
            MessageBox.Show("Open a Python or JavaScript file before starting the debugger.", "No debug target");
            return;
        }
        if (_isDirty && !SaveCurrentFile()) return;
        var extension = Path.GetExtension(_currentFile).ToLowerInvariant();
        var file = _currentFile.Replace("'", "''");
        var directory = (Path.GetDirectoryName(_currentFile) ?? Environment.CurrentDirectory).Replace("'", "''");
        string command;
        if (extension == ".py")
            command = $"Set-Location -LiteralPath '{directory}'; python -m pdb '{file}'";
        else if (extension is ".js" or ".mjs" or ".cjs")
            command = $"Set-Location -LiteralPath '{directory}'; node inspect '{file}'";
        else
        {
            MessageBox.Show("Interactive debugging is currently available for Python (pdb) and Node.js (node inspect). C# builds can be run with the Build command.", "Debugger support");
            return;
        }
        ConPtyTerminal.StartupCommandLine = "pwsh.exe -NoExit -Command \"" + command.Replace("\"", "\\\"") + "\"";
        ConPtyTerminal.RestartTerm();
        WorkspaceTabs.SelectedItem = TerminalTab;
        StatusText.Text = extension == ".py" ? "Python debugger started · use pdb commands: n, s, c, p, b, q" : "Node debugger started · use node inspector commands: cont, next, step, repl, list, quit";
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            MessageBox.Show("Open a project folder before building.", "No project"); return;
        }
        var project = Directory.GetFiles(_projectRoot, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                      ?? Directory.GetFiles(_projectRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (project is null)
        {
            AppendTerminal("No .sln or .csproj found in the project root. Use the shell for JavaScript or Python build commands.");
            return;
        }
        var info = new ProcessStartInfo
        {
            FileName = "dotnet", WorkingDirectory = _projectRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        info.ArgumentList.Add("build"); info.ArgumentList.Add(project); StartToolProcess(info, "Build");
    }
    private void StartToolProcess(ProcessStartInfo info, string operation)
    {
        if (_runProcess is { HasExited: false })
        {
            MessageBox.Show("A process is already running. Stop it before starting another.", "Process running"); return;
        }
        try
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendTerminal(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendTerminal(e.Data); };
            process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendTerminal($"[{operation} process exited: code {process.ExitCode}]");
                StatusText.Text = $"{operation} finished (exit {process.ExitCode})";
                if (ReferenceEquals(_runProcess, process)) _runProcess = null;
                process.Dispose();
            }));
            process.Start(); _runProcess = process;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            AppendTerminal($"[{operation} started]"); StatusText.Text = $"{operation} running…";
            if (!_terminalVisible) ToggleTerminal_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex) { AppendTerminal($"Could not start {operation}: {ex.Message}"); StatusText.Text = $"{operation} failed to start"; }
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { if (_runProcess is { HasExited: false }) _runProcess.Kill(entireProcessTree: true); StatusText.Text = "Stop requested"; }
        catch (Exception ex) { AppendTerminal($"Stop failed: {ex.Message}"); }
    }

    private async Task InitializeMonacoAsync()
    {
        try
        {
            await MonacoEditor.EnsureCoreWebView2Async();
            MonacoEditor.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try { var message = JsonDocument.Parse(e.WebMessageAsJson).RootElement; if (message.TryGetProperty("text", out var value)) { _monacoText = value.GetString() ?? string.Empty; _isDirty = _currentFile is not null; if (_isDirty) CurrentFileText.Text = $"{Path.GetFileName(_currentFile)} •"; } } catch { }
            };
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "monaco.html");
            if (!File.Exists(htmlPath)) throw new FileNotFoundException("Monaco web assets were not published.", htmlPath);
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MonacoEditor.CoreWebView2.NavigationCompleted += (_, args) => navigation.TrySetResult(args.IsSuccess);
            MonacoEditor.NavigateToString(File.ReadAllText(htmlPath));
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Monaco page failed to load.");
            _monacoReady = true;
            await SetMonacoTextAsync(_monacoText, _currentFile);
        }
        catch (Exception ex) { StatusText.Text = "Monaco initialization failed: " + ex.Message; }
    }

    private async Task SetMonacoTextAsync(string text, string? path)
    {
        if (!_monacoReady) return;
        var language = Path.GetExtension(path ?? "").ToLowerInvariant() switch { ".js" or ".mjs" or ".cjs" => "javascript", ".py" => "python", ".cs" => "csharp", ".json" => "json", ".xml" or ".xaml" => "xml", _ => "plaintext" };
        await MonacoEditor.ExecuteScriptAsync("window.studioSetValue(" + JsonSerializer.Serialize(text) + "," + JsonSerializer.Serialize(language) + ");");
    }

    private async void OpenMonaco_Click(object sender, RoutedEventArgs e)
    {
        if (!_monacoReady) { MessageBox.Show("Monaco is still initializing. It also requires internet access to load its editor engine.", "Monaco Editor"); return; }
        if (WorkspaceTabs.SelectedItem == EditorTab) _monacoText = EditorText.Text;
        await SetMonacoTextAsync(_monacoText, _currentFile);
        WorkspaceTabs.SelectedItem = MonacoTab;
    }

    private void NativePowerShell_Click(object sender, RoutedEventArgs e) { ConPtyTerminal.StartupCommandLine = "pwsh.exe"; ConPtyTerminal.RestartTerm(); }
    private void NativeCommandPrompt_Click(object sender, RoutedEventArgs e) { ConPtyTerminal.StartupCommandLine = "cmd.exe"; ConPtyTerminal.RestartTerm(); }
    private void NativeRestart_Click(object sender, RoutedEventArgs e) => ConPtyTerminal.RestartTerm();

    private void ShowEditor_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = EditorTab;
    private void Show3D_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = ModelerTab;
    private void ShowExtensions_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = ExtensionsTab;
    private void RefreshExtensionStatus()
    {
        JsStatus.Text = _settings.JavaScriptEnabled ? "Enabled" : "Disabled";
        PythonStatus.Text = _settings.PythonEnabled ? "Enabled" : "Disabled";
        CSharpStatus.Text = _settings.CSharpEnabled ? "Enabled" : "Disabled";
    }
    private void ToggleJavaScript_Click(object sender, RoutedEventArgs e) { _settings.JavaScriptEnabled = !_settings.JavaScriptEnabled; RefreshExtensionStatus(); PersistSettings(); }
    private void TogglePython_Click(object sender, RoutedEventArgs e) { _settings.PythonEnabled = !_settings.PythonEnabled; RefreshExtensionStatus(); PersistSettings(); }
    private void ToggleCSharp_Click(object sender, RoutedEventArgs e) { _settings.CSharpEnabled = !_settings.CSharpEnabled; RefreshExtensionStatus(); PersistSettings(); }

    private void InitializeViewport()
    {
        if (ModelViewport.Children.Count > 0) return;
        ModelViewport.Camera = new PerspectiveCamera(new Point3D(5, 4, 7), new Vector3D(-5, -3.5, -7), new Vector3D(0, 1, 0), 45);
        _world = new Model3DGroup();
        _world.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 160)));
        _world.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -3)));
        ModelViewport.Children.Add(new ModelVisual3D { Content = _world });
        AddSceneObject("Cube");
    }
    private void AddPrimitive_Click(object sender, RoutedEventArgs e)
    {
        var shape = (ShapePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Cube";
        AddSceneObject(shape);
    }
    private void AddSceneObject(string shape)
    {
        var number = _scene.Count + 1;
        var mesh = CreatePrimitiveMesh(shape);
        var material = CreateMaterial(_scene.Count % 2 == 0 ? "#718EFF" : "#47B8AD");
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        var item = new SceneObject { Name = $"{shape} {number}", Shape = shape, Mesh = mesh, Model = model, ColorHex = _scene.Count % 2 == 0 ? "#718EFF" : "#47B8AD" };
        UpdateObjectTransform(item); _scene.Add(item); _world.Children.Add(model);
        RefreshSceneList(item.Name); StatusText.Text = $"{item.Name} added to the 3D scene";
    }
    private static MeshGeometry3D CreatePrimitiveMesh(string shape)
    {
        if (shape == "Sphere") return CreateSphereMesh(0.75, 24, 32);
        if (shape == "Plane") return new MeshGeometry3D { Positions = new Point3DCollection { new(-1,0,-1), new(1,0,-1), new(1,0,1), new(-1,0,1) }, TriangleIndices = new Int32Collection { 0,1,2,0,2,3 } };
        if (shape == "Cylinder" || shape == "Cone")
        {
            const int n = 32; var p = new Point3DCollection(); var t = new Int32Collection();
            for (var i=0;i<n;i++) { var a=2*Math.PI*i/n; p.Add(new Point3D(Math.Cos(a)*0.7,-0.8,Math.Sin(a)*0.7)); p.Add(new Point3D(shape=="Cone"?0:Math.Cos(a)*0.7,0.8,shape=="Cone"?0:Math.Sin(a)*0.7)); }
            for (var i=0;i<n;i++) { var j=(i+1)%n; t.Add(2*i);t.Add(2*j);t.Add(2*i+1); if(shape=="Cylinder"){t.Add(2*j);t.Add(2*j+1);t.Add(2*i+1);} }
            return new MeshGeometry3D { Positions=p, TriangleIndices=t };
        }
        if (shape == "Torus")
        {
            const int major=40, minor=16; var p=new Point3DCollection(); var t=new Int32Collection();
            for(var i=0;i<=major;i++){var u=2*Math.PI*i/major;for(var j=0;j<=minor;j++){var v=2*Math.PI*j/minor;var r=0.55+0.2*Math.Cos(v);p.Add(new Point3D(r*Math.Cos(u),0.2*Math.Sin(v),r*Math.Sin(u)));}}
            for(var i=0;i<major;i++)for(var j=0;j<minor;j++){var a=i*(minor+1)+j;var b=a+minor+1;t.Add(a);t.Add(b);t.Add(a+1);t.Add(a+1);t.Add(b);t.Add(b+1);}
            return new MeshGeometry3D { Positions=p, TriangleIndices=t };
        }
        return CreateCubeMesh(1.0);
    }
    private static MeshGeometry3D CreateCubeMesh(double size)
    {
        var h = size / 2;
        var positions = new Point3DCollection
        {
            new(-h,-h,-h), new(h,-h,-h), new(h,h,-h), new(-h,h,-h),
            new(-h,-h,h), new(h,-h,h), new(h,h,h), new(-h,h,h)
        };
        var indices = new Int32Collection
        {
            0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
            3,7,6, 3,6,2, 1,2,6, 1,6,5, 0,4,7, 0,7,3
        };
        return new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
    }
    private static MeshGeometry3D CreateSphereMesh(double radius, int latitude, int longitude)
    {
        var positions = new Point3DCollection(); var triangles = new Int32Collection();
        for (var lat = 0; lat <= latitude; lat++)
        {
            var theta = Math.PI * lat / latitude;
            for (var lon = 0; lon <= longitude; lon++)
            {
                var phi = 2 * Math.PI * lon / longitude;
                positions.Add(new Point3D(radius * Math.Sin(theta) * Math.Cos(phi), radius * Math.Cos(theta),
                    radius * Math.Sin(theta) * Math.Sin(phi)));
            }
        }
        for (var lat = 0; lat < latitude; lat++)
            for (var lon = 0; lon < longitude; lon++)
            {
                var current = lat * (longitude + 1) + lon; var next = current + longitude + 1;
                triangles.Add(current); triangles.Add(next); triangles.Add(current + 1);
                triangles.Add(current + 1); triangles.Add(next); triangles.Add(next + 1);
            }
        return new MeshGeometry3D { Positions = positions, TriangleIndices = triangles };
    }
    private static Material CreateMaterial(string colorHex)
    {
        var color = (Color)(new ColorConverter().ConvertFromString(colorHex) ?? Colors.SlateGray);
        return new DiffuseMaterial(new SolidColorBrush(color));
    }
    private static void UpdateObjectMaterial(SceneObject item)
    {
        var material = CreateMaterial(item.ColorHex);
        item.Model.Material = material;
        item.Model.BackMaterial = material;
    }
    private static void UpdateObjectTransform(SceneObject item)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(item.ScaleX, item.ScaleY, item.ScaleZ));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), item.RotationX)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), item.RotationY)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), item.RotationZ)));
        transform.Children.Add(new TranslateTransform3D(item.X, item.Y, item.Z));
        item.Model.Transform = transform;
    }
    private void RefreshSceneList(string? selectedName = null)
    {
        _sceneNames.Clear(); foreach (var item in _scene) _sceneNames.Add(item.Name);
        SceneList.ItemsSource = null;
        SceneList.ItemsSource = _sceneNames;
        if (selectedName is not null) SceneList.SelectedItem = selectedName;
        else if (_scene.Count > 0) SceneList.SelectedIndex = _scene.Count - 1;
    }
    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) return;
        ObjectNameInput.Text = item.Name;
        ObjectX.Text = item.X.ToString(CultureInfo.InvariantCulture);
        ObjectY.Text = item.Y.ToString(CultureInfo.InvariantCulture);
        ObjectZ.Text = item.Z.ToString(CultureInfo.InvariantCulture);
        ScaleX.Text = item.ScaleX.ToString(CultureInfo.InvariantCulture);
        ScaleY.Text = item.ScaleY.ToString(CultureInfo.InvariantCulture);
        ScaleZ.Text = item.ScaleZ.ToString(CultureInfo.InvariantCulture);
        RotationX.Text = item.RotationX.ToString(CultureInfo.InvariantCulture);
        RotationY.Text = item.RotationY.ToString(CultureInfo.InvariantCulture);
        RotationZ.Text = item.RotationZ.ToString(CultureInfo.InvariantCulture);
        ObjectColor.Text = item.ColorHex;
    }
    private void ApplyTransform_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select an object in the scene hierarchy first."); return; }
        if (!TryNumber(ObjectX.Text, out var x) || !TryNumber(ObjectY.Text, out var y) ||
            !TryNumber(ObjectZ.Text, out var z) || !TryNumber(ScaleX.Text, out var sx) ||
            !TryNumber(ScaleY.Text, out var sy) || !TryNumber(ScaleZ.Text, out var sz) ||
            !TryNumber(RotationX.Text, out var rotationX) || !TryNumber(RotationY.Text, out var rotationY) || !TryNumber(RotationZ.Text, out var rotationZ) ||
            new ColorConverter().ConvertFromString(ObjectColor.Text) is not Color color)
        {
            MessageBox.Show("Enter valid numeric transform values.", "Invalid transform"); return;
        }
        item.Name = string.IsNullOrWhiteSpace(ObjectNameInput.Text) ? item.Name : ObjectNameInput.Text.Trim();
        item.X = x; item.Y = y; item.Z = z; item.ScaleX = sx; item.ScaleY = sy; item.ScaleZ = sz;
        item.RotationX = rotationX; item.RotationY = rotationY; item.RotationZ = rotationZ; item.ColorHex = color.ToString();
        UpdateObjectTransform(item); UpdateObjectMaterial(item); RefreshSceneList(item.Name);
    }
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    private void SubdivideMesh_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select an object first."); return; }
        if (item.Mesh.TriangleIndices.Count == 0) { MessageBox.Show("The selected mesh has no triangles."); return; }

        var positions = item.Mesh.Positions.ToList();
        var indices = new Int32Collection();
        var midpointCache = new Dictionary<(int, int), int>();
        int Midpoint(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (midpointCache.TryGetValue(key, out var existing)) return existing;
            var p1 = positions[a]; var p2 = positions[b];
            var index = positions.Count;
            positions.Add(new Point3D((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2, (p1.Z + p2.Z) / 2));
            midpointCache[key] = index;
            return index;
        }

        for (var i = 0; i + 2 < item.Mesh.TriangleIndices.Count; i += 3)
        {
            var a = item.Mesh.TriangleIndices[i];
            var b = item.Mesh.TriangleIndices[i + 1];
            var c = item.Mesh.TriangleIndices[i + 2];
            var ab = Midpoint(a, b); var bc = Midpoint(b, c); var ca = Midpoint(c, a);
            indices.Add(a); indices.Add(ab); indices.Add(ca);
            indices.Add(ab); indices.Add(b); indices.Add(bc);
            indices.Add(ca); indices.Add(bc); indices.Add(c);
            indices.Add(ab); indices.Add(bc); indices.Add(ca);
        }
        item.Mesh = new MeshGeometry3D { Positions = new Point3DCollection(positions), TriangleIndices = indices };
        item.Model.Geometry = item.Mesh;
        StatusText.Text = $"{item.Name}: subdivided to {indices.Count / 3} triangles";
    }

    private void DuplicateObject_Click(object sender, RoutedEventArgs e)
    {
        var source = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (source is null) return;
        var mesh = CreatePrimitiveMesh(source.Shape);
        var material = CreateMaterial(source.ColorHex);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        var copy = new SceneObject
        {
            Name = source.Name + " Copy", Shape = source.Shape, Mesh = mesh, Model = model,
            X = source.X + 1, Y = source.Y, Z = source.Z, ScaleX = source.ScaleX, ScaleY = source.ScaleY,
            ScaleZ = source.ScaleZ, RotationX = source.RotationX, RotationY = source.RotationY, RotationZ = source.RotationZ, ColorHex = source.ColorHex
        };
        UpdateObjectTransform(copy); _scene.Add(copy); _world.Children.Add(model); RefreshSceneList(copy.Name);
    }
    private void DeleteObject_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) return;
        _world.Children.Remove(item.Model); _scene.Remove(item); RefreshSceneList();
    }
    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Studio 3D scene (*.studio3d.json)|*.studio3d.json|JSON file (*.json)|*.json", FileName = "scene.studio3d.json" };
        if (dialog.ShowDialog() != true) return;
        var document = new SceneFileData
        {
            FormatVersion = 2,
            Objects = _scene.Select(x => new SceneObjectFileData
            {
                Name = x.Name, Shape = x.Shape, X = x.X, Y = x.Y, Z = x.Z,
                ScaleX = x.ScaleX, ScaleY = x.ScaleY, ScaleZ = x.ScaleZ,
                RotationX = x.RotationX, RotationY = x.RotationY, RotationZ = x.RotationZ,
                ColorHex = x.ColorHex,
                Vertices = x.Mesh.Positions.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
                Triangles = x.Mesh.TriangleIndices.ToArray()
            }).ToList()
        };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        StatusText.Text = "3D scene saved (including mesh geometry and materials)";
    }
    private void OpenScene_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Studio 3D scene (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var doc = JsonSerializer.Deserialize<SceneFileData>(File.ReadAllText(dialog.FileName));
            if (doc is null || doc.FormatVersion is < 1 or > 2) throw new InvalidDataException("Unsupported or invalid Studio 3D scene.");
            foreach (var old in _scene.ToList()) _world.Children.Remove(old.Model);
            _scene.Clear(); _sceneNames.Clear();
            foreach (var saved in doc.Objects)
            {
                var mesh = CreatePrimitiveMesh(saved.Shape);
                if (saved.Vertices is { Length: > 0 } vertices && saved.Triangles is { Length: > 0 } triangles)
                {
                    var custom = new MeshGeometry3D();
                    foreach (var vertex in vertices)
                    {
                        if (vertex.Length != 3) throw new InvalidDataException("Invalid vertex in scene file.");
                        custom.Positions.Add(new Point3D(vertex[0], vertex[1], vertex[2]));
                    }
                    foreach (var index in triangles)
                    {
                        if (index < 0 || index >= custom.Positions.Count) throw new InvalidDataException("Invalid triangle index in scene file.");
                        custom.TriangleIndices.Add(index);
                    }
                    mesh = custom;
                }
                var colorHex = string.IsNullOrWhiteSpace(saved.ColorHex) ? "#718EFF" : saved.ColorHex;
                var material = CreateMaterial(colorHex);
                var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
                var item = new SceneObject
                {
                    Name = saved.Name, Shape = saved.Shape, Mesh = mesh, Model = model,
                    X = saved.X, Y = saved.Y, Z = saved.Z, ScaleX = saved.ScaleX,
                    ScaleY = saved.ScaleY, ScaleZ = saved.ScaleZ,
                    RotationX = saved.RotationX, RotationY = saved.RotationY, RotationZ = saved.RotationZ,
                    ColorHex = colorHex
                };
                UpdateObjectTransform(item); _scene.Add(item); _world.Children.Add(model);
            }
            RefreshSceneList(); StatusText.Text = "3D scene loaded";
        }
        catch (Exception ex) { MessageBox.Show($"Could not open scene:\n{ex.Message}", "Scene error"); }
    }

    private void Export3D_Click(object sender, RoutedEventArgs e)
    {
        if (_scene.Count == 0) { MessageBox.Show("Add an object to the scene before exporting."); return; }
        var dialog = new SaveFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj|ASCII STL (*.stl)|*.stl", FileName = "scene.obj" };
        if (dialog.ShowDialog() != true) return;
        var content = Path.GetExtension(dialog.FileName).Equals(".stl", StringComparison.OrdinalIgnoreCase) ? ExportStl() : ExportObj();
        File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
        StatusText.Text = $"Exported {Path.GetFileName(dialog.FileName)}";
    }
    private string ExportObj()
    {
        var sb = new StringBuilder("# Studio.exe OBJ export\n"); var offset = 1;
        foreach (var item in _scene)
        {
            sb.AppendLine($"o {SanitizeObjectName(item.Name)}");
            foreach (var point in item.Mesh.Positions)
            {
                var p = item.Model.Transform.Transform(point);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"v {p.X:0.######} {p.Y:0.######} {p.Z:0.######}"));
            }
            var indices = item.Mesh.TriangleIndices;
            for (var i = 0; i + 2 < indices.Count; i += 3)
                sb.AppendLine($"f {indices[i] + offset} {indices[i + 1] + offset} {indices[i + 2] + offset}");
            offset += item.Mesh.Positions.Count;
        }
        return sb.ToString();
    }
    private string ExportStl()
    {
        var sb = new StringBuilder("solid StudioScene\n");
        foreach (var item in _scene)
        {
            var points = item.Mesh.Positions.Select(p => item.Model.Transform.Transform(p)).ToArray();
            var indices = item.Mesh.TriangleIndices;
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var a = points[indices[i]]; var b = points[indices[i + 1]]; var c = points[indices[i + 2]];
                var normal = Vector3D.CrossProduct(b - a, c - a); if (normal.Length > 0) normal.Normalize();
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $" facet normal {normal.X:0.######} {normal.Y:0.######} {normal.Z:0.######}"));
                sb.AppendLine("  outer loop");
                foreach (var p in new[] { a, b, c })
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"   vertex {p.X:0.######} {p.Y:0.######} {p.Z:0.######}"));
                sb.AppendLine("  endloop\n endfacet");
            }
        }
        sb.AppendLine("endsolid StudioScene"); return sb.ToString();
    }
    private static string SanitizeObjectName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show($"{Title}\nWindows development workspace for JavaScript, Python, C#, a real shell session, and a lightweight 3D scene editor.\n\nIncludes Monaco, contextual Python/C# IntelliSense, ConPTY, terminal debugging for Python/Node, and a lightweight 3D scene editor. See README for language-server and advanced modeling limits.",
            "About Studio.exe", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ctrl && e.Key == Key.S) { SaveCurrentFile(); e.Handled = true; }
        else if (ctrl && e.Key == Key.P) { OpenFile_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OpenProject_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.F)
        {
            var query = Microsoft.VisualBasic.Interaction.InputBox("Find text in the current file:", "Find");
            if (query.Length > 0)
            {
                var start = Math.Max(0, EditorText.SelectionStart + EditorText.SelectionLength);
                var index = EditorText.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) index = EditorText.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) { EditorText.Focus(); EditorText.Select(index, query.Length); }
                else StatusText.Text = "Text not found";
            }
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Oem3) { ToggleTerminal_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F5) { Run_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.E) { ShowEditor_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardOrSave()) { e.Cancel = true; return; }
        _terminal.Dispose();
        try { if (_runProcess is { HasExited: false }) _runProcess.Kill(entireProcessTree: true); } catch { }
    }

    private sealed record EditorDiagnostic(int Line, int Column, string Message, string Severity);

    private sealed class SceneFileData
    {
        public int FormatVersion { get; set; } = 2;
        public List<SceneObjectFileData> Objects { get; set; } = [];
    }
    private sealed class SceneObjectFileData
    {
        public string Name { get; set; } = "Object";
        public string Shape { get; set; } = "Cube";
        public double X { get; set; } public double Y { get; set; } public double Z { get; set; }
        public double ScaleX { get; set; } = 1; public double ScaleY { get; set; } = 1; public double ScaleZ { get; set; } = 1;
        public double RotationX { get; set; } public double RotationY { get; set; } public double RotationZ { get; set; }
        public string ColorHex { get; set; } = "#718EFF";
        public double[][]? Vertices { get; set; }
        public int[]? Triangles { get; set; }
    }
    private sealed class SceneObject
    {
        public string Name { get; set; } = "Object";
        public string Shape { get; set; } = "Cube";
        public MeshGeometry3D Mesh { get; set; } = new();
        public GeometryModel3D Model { get; set; } = new(new MeshGeometry3D(), new DiffuseMaterial());
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public double ScaleZ { get; set; } = 1;
        public double RotationX { get; set; }
        public double RotationY { get; set; }
        public double RotationZ { get; set; }
        public string ColorHex { get; set; } = "#718EFF";
    }
}
