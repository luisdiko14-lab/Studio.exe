using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
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
    private readonly ObservableCollection<VertexEditRow> _vertexRows = [];
    private readonly ObservableCollection<OpenDocument> _openDocuments = [];
    private OpenDocument? _currentDocument;
    private bool _suppressDocumentSelection;
    private CancellationTokenSource? _searchCancellation;
    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Point _modelViewportMouseDown;
    private StdioProtocolProcess? _languageServer, _debugAdapter;
    private string? _languageServerId, _documentUri;
    private AppSettings _settings;
    private string? _projectRoot, _currentDirectory, _currentFile;
    private bool _isLoadingEditor, _isDirty, _terminalVisible = true, _monacoReady, _wpfEditorChanged;
    private string _monacoText = string.Empty;
    private Process? _runProcess;
    private Model3DGroup _world = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        ExplorerList.ItemsSource = _explorerEntries;
        SceneList.ItemsSource = _sceneNames;
        VertexGrid.ItemsSource = _vertexRows;
        OpenDocumentsList.ItemsSource = _openDocuments;
        _animationTimer.Tick += AnimationTimer_Tick;
        WizardRoot.Visibility = _settings.SetupCompleted ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceRoot.Visibility = _settings.SetupCompleted ? Visibility.Visible : Visibility.Collapsed;
        StudioNameInput.Text = _settings.StudioName;
        StudioNamePreview.Text = _settings.StudioName;
        DefaultFolderInput.Text = _settings.DefaultProjectDirectory;
        ApplyBranding();
        RefreshExtensionStatus();
        RefreshLanguageServerStatus();
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

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardOrSave()) return;
        var folder = new OpenFolderDialog
        {
            Title = "Choose the parent folder for the new project",
            InitialDirectory = Directory.Exists(_settings.DefaultProjectDirectory)
                ? _settings.DefaultProjectDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (folder.ShowDialog() != true) return;

        var name = Microsoft.VisualBasic.Interaction.InputBox("Project folder name:", "New Project", "MyProject").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        name = name.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            MessageBox.Show("Enter a valid project folder name.", "New Project");
            return;
        }

        var template = Microsoft.VisualBasic.Interaction.InputBox(
            "Choose a template: JavaScript, Python, C# Console, or Web",
            "Project Template", "JavaScript").Trim();
        if (template.Length == 0) return;
        template = template.ToLowerInvariant() switch
        {
            "javascript" or "js" or "node" => "JavaScript",
            "python" or "py" => "Python",
            "c#" or "csharp" or "c# console" or "console" => "C# Console",
            "web" or "html" or "website" => "Web",
            _ => string.Empty
        };
        if (template.Length == 0)
        {
            MessageBox.Show("Choose JavaScript, Python, C# Console, or Web.", "Unknown template");
            return;
        }

        var projectRoot = Path.Combine(folder.FolderName, name);
        if (Directory.Exists(projectRoot) || File.Exists(projectRoot))
        {
            MessageBox.Show("A file or folder with that name already exists in the selected location.", "New Project");
            return;
        }

        try
        {
            Directory.CreateDirectory(projectRoot);
            var entryFile = template switch
            {
                "JavaScript" => CreateJavaScriptTemplate(projectRoot, name),
                "Python" => CreatePythonTemplate(projectRoot),
                "C# Console" => CreateCSharpTemplate(projectRoot, name),
                _ => CreateWebTemplate(projectRoot)
            };
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"),
                "node_modules/\n.DS_Store\n.vscode/\n.idea/\nbin/\nobj/\n__pycache__/\n.venv/\nartifacts/\n", new UTF8Encoding(false));

            _projectRoot = projectRoot;
            _currentDirectory = projectRoot;
            GitRootText.Text = projectRoot;
            ProjectStatusText.Text = projectRoot;
            _settings.DefaultProjectDirectory = folder.FolderName;
            PersistSettings();
            RefreshExplorer();
            OpenFile(Path.Combine(projectRoot, entryFile));
            StatusText.Text = template + " project created";
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true); } catch { }
            MessageBox.Show("Could not create the project:\n" + ex.Message, "New Project failed");
        }
    }

    private static string CreateJavaScriptTemplate(string root, string name)
    {
        var packageName = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.');
        if (packageName.Length == 0) packageName = "my-project";
        var package = new
        {
            name = packageName,
            version = "1.0.0",
            private = true,
            type = "module",
            scripts = new { start = "node index.js" }
        };
        File.WriteAllText(Path.Combine(root, "package.json"),
            JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "index.js"),
            "console.log('Hello from " + name.Replace("'", "\\'") + "!');\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "README.md"),
            "# " + name + "\n\nRun npm start to start the project.\n", new UTF8Encoding(false));
        return "index.js";
    }

    private static string CreatePythonTemplate(string root)
    {
        File.WriteAllText(Path.Combine(root, "main.py"),
            "def main() -> None:\n    print('Hello from Python!')\n\n\nif __name__ == '__main__':\n    main()\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "requirements.txt"), "# Add project dependencies here.\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "README.md"),
            "# Python project\n\nRun python main.py to start.\n", new UTF8Encoding(false));
        return "main.py";
    }

    private static string CreateCSharpTemplate(string root, string name)
    {
        var safe = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        if (safe.Length == 0 || (!char.IsLetter(safe[0]) && safe[0] != '_')) safe = "_" + safe;
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;
        File.WriteAllText(Path.Combine(root, safe + ".csproj"), csproj + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "Program.cs"),
            "Console.WriteLine(\"Hello from " + safe + "!\");" + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "README.md"),
            "# " + safe + "\n\nBuild with dotnet build and run with dotnet run.\n", new UTF8Encoding(false));
        return safe + ".csproj";
    }

    private static string CreateWebTemplate(string root)
    {
        File.WriteAllText(Path.Combine(root, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>My Web Project</title>
              <link rel="stylesheet" href="styles.css">
            </head>
            <body>
              <main>
                <h1>Hello, world!</h1>
                <p>Your new Studio.exe web project is ready.</p>
                <button id="hello">Click me</button>
              </main>
              <script type="module" src="main.js"></script>
            </body>
            </html>
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "styles.css"),
            "body{font:16px system-ui;background:#111827;color:#f8fafc;display:grid;min-height:100vh;place-items:center}main{max-width:42rem;padding:2rem}button{padding:.7rem 1rem;border-radius:.5rem;border:0;cursor:pointer}\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "main.js"),
            "document.querySelector('#hello').addEventListener('click',()=>alert('Your web project is running!'));\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "README.md"), "# Web project\n\nOpen index.html in a browser.\n", new UTF8Encoding(false));
        return "main.js";
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
        _projectRoot = dialog.FolderName; _currentDirectory = _projectRoot; GitRootText.Text = _projectRoot;
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
        try
        {
            var fullPath = Path.GetFullPath(path);
            var document = _openDocuments.FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (document is null)
            {
                document = new OpenDocument { FilePath = fullPath, Content = File.ReadAllText(fullPath) };
                _openDocuments.Add(document);
            }

            if (ReferenceEquals(OpenDocumentsList.SelectedItem, document))
                _ = ActivateDocumentAsync(document);
            else
                OpenDocumentsList.SelectedItem = document;
        }
        catch (Exception ex) { MessageBox.Show($"Could not open file:\n{ex.Message}", "Open file failed"); }
    }

    private async void OpenDocumentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDocumentSelection || OpenDocumentsList.SelectedItem is not OpenDocument document) return;
        await ActivateDocumentAsync(document);
    }

    private async Task ActivateDocumentAsync(OpenDocument document)
    {
        if (_currentDocument is not null && !ReferenceEquals(_currentDocument, document))
            CaptureActiveDocument();

        _currentDocument = document;
        _currentFile = document.FilePath;
        _isDirty = document.IsDirty;
        _currentDirectory = Path.GetDirectoryName(document.FilePath);
        _monacoText = document.Content;
        _isLoadingEditor = true;
        EditorText.Text = document.Content;
        _wpfEditorChanged = false;
        _isLoadingEditor = false;
        CurrentFileText.Text = document.DisplayName;
        ProjectStatusText.Text = _projectRoot ?? _currentDirectory ?? "No project open";
        WorkspaceTabs.SelectedItem = MonacoTab;
        await SetMonacoTextAsync(document.Content, document.FilePath);
        StatusText.Text = $"Opened {Path.GetFileName(document.FilePath)}";
        RefreshExplorer();
    }

    private void CaptureActiveDocument()
    {
        if (_currentDocument is null) return;
        var content = WorkspaceTabs.SelectedItem == MonacoTab ? _monacoText : EditorText.Text;
        _currentDocument.Content = content;
        _currentDocument.IsDirty = _isDirty;
    }

    private async void CloseCurrentDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocument is null) return;
        var document = _currentDocument;
        if (document.IsDirty)
        {
            var answer = MessageBox.Show($"Save changes to {Path.GetFileName(document.FilePath)} before closing?",
                "Close document", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes && !SaveDocument(document)) return;
        }
        var uri = new Uri(Path.GetFullPath(document.FilePath)).AbsoluteUri;
        if (_monacoReady) await MonacoEditor.ExecuteScriptAsync("window.studioCloseDocument(" + JsonSerializer.Serialize(uri) + ");");

        var index = _openDocuments.IndexOf(document);
        _suppressDocumentSelection = true;
        _openDocuments.Remove(document);
        OpenDocumentsList.SelectedItem = null;
        _currentDocument = null;
        _currentFile = null;
        _isDirty = false;
        if (_openDocuments.Count > 0)
        {
            var next = _openDocuments[Math.Clamp(index, 0, _openDocuments.Count - 1)];
            OpenDocumentsList.SelectedItem = next;
            _suppressDocumentSelection = false;
            await ActivateDocumentAsync(next);
        }
        else
        {
            _monacoText = string.Empty;
            _isLoadingEditor = true; EditorText.Clear(); _isLoadingEditor = false;
            _wpfEditorChanged = false;
            CurrentFileText.Text = "No file open";
            StatusText.Text = "All documents closed";
            _suppressDocumentSelection = false;
        }
    }

    private void EditorText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEditor) return;
        _wpfEditorChanged = true;
        _isDirty = _currentFile is not null;
        if (_currentDocument is not null)
        {
            _currentDocument.Content = EditorText.Text;
            _currentDocument.IsDirty = _isDirty;
        }
        if (_isDirty) CurrentFileText.Text = _currentDocument?.DisplayName ?? (Path.GetFileName(_currentFile) + " •");
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrentFile();
    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            InitialDirectory = Directory.Exists(_currentDirectory) ? _currentDirectory : "",
            FileName = _currentFile is null ? "new-file.txt" : Path.GetFileName(_currentFile)
        };
        if (dialog.ShowDialog() == true)
        {
            _currentFile = Path.GetFullPath(dialog.FileName);
            if (_currentDocument is not null) _currentDocument.FilePath = _currentFile;
            SaveCurrentFile();
            if (_currentDocument is not null) _ = SetMonacoTextAsync(_currentDocument.Content, _currentDocument.FilePath);
        }
    }
    private bool SaveCurrentFile()
    {
        if (_currentDocument is null)
        {
            if (_currentFile is null) { SaveAs_Click(this, new RoutedEventArgs()); return !_isDirty; }
            _currentDocument = new OpenDocument { FilePath = _currentFile };
        }
        CaptureActiveDocument();
        return SaveDocument(_currentDocument);
    }

    private bool SaveDocument(OpenDocument document)
    {
        try
        {
            File.WriteAllText(document.FilePath, document.Content, new UTF8Encoding(false));
            document.IsDirty = false;
            if (ReferenceEquals(_currentDocument, document))
            {
                _isDirty = false;
                _monacoText = document.Content;
                CurrentFileText.Text = document.DisplayName;
            }
            StatusText.Text = "Saved " + Path.GetFileName(document.FilePath);
            RefreshExplorer();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save {Path.GetFileName(document.FilePath)}:\n{ex.Message}", "Save failed");
            return false;
        }
    }

    private bool ConfirmDiscardOrSave()
    {
        CaptureActiveDocument();
        var dirtyDocuments = _openDocuments.Where(document => document.IsDirty).ToList();
        if (dirtyDocuments.Count == 0) return true;
        var answer = MessageBox.Show($"Save changes to {dirtyDocuments.Count} open document(s) before continuing?",
            "Unsaved documents", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes)
        {
            foreach (var document in dirtyDocuments)
                if (!SaveDocument(document)) return false;
        }
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

    private async void Debug_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null)
        {
            MessageBox.Show("Open a Python, JavaScript, or C# project file before starting the debugger.", "No debug target");
            return;
        }
        if (_isDirty && !SaveCurrentFile()) return;
        if (!_monacoReady)
        {
            MessageBox.Show("The Monaco editor is still initializing. Try again once the editor tab is ready.", "Debugger");
            return;
        }

        var extension = Path.GetExtension(_currentFile).ToLowerInvariant();
        var workingDirectory = _projectRoot ?? Path.GetDirectoryName(_currentFile)!;
        try
        {
            if (extension == ".py")
            {
                var python = FindTool("python.exe", "python");
                if (python is null) throw new FileNotFoundException("Python was not found on PATH.");
                var launch = new
                {
                    name = "Launch Python",
                    type = "python",
                    request = "launch",
                    program = Path.GetFullPath(_currentFile),
                    cwd = workingDirectory,
                    python,
                    console = "internalConsole",
                    justMyCode = true,
                    stopOnEntry = false,
                    redirectOutput = true,
                    subProcess = false
                };
                StartDebugAdapter(python, new[] { "-m", "debugpy.adapter" }, workingDirectory, "python", launch, "Python", _currentFile);
                return;
            }

            if (extension is ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx")
            {
                var node = FindTool("node.exe", "node");
                var jsAdapter = FindTool("dapDebugServer.js");
                if (node is null) throw new FileNotFoundException("Node.js was not found on PATH.");
                if (jsAdapter is null) throw new FileNotFoundException("The Microsoft JavaScript DAP adapter is not installed. Use Extensions → Install / Update.");
                var launch = new
                {
                    name = "Launch Node.js",
                    type = "pwa-node",
                    request = "launch",
                    program = Path.GetFullPath(_currentFile),
                    cwd = workingDirectory,
                    runtimeExecutable = node,
                    console = "internalConsole",
                    skipFiles = new[] { "<node_internals>/**" },
                    sourceMaps = true,
                    smartStep = true
                };
                StartDebugAdapter(node, new[] { jsAdapter }, workingDirectory, "pwa-node", launch, "Node.js", _currentFile);
                return;
            }

            if (extension is ".cs" or ".csproj" or ".sln")
            {
                var project = extension == ".csproj" ? _currentFile :
                    Directory.EnumerateFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories)
                        .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                          && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
                if (project is null) throw new FileNotFoundException("Could not find a C# project (.csproj) in the opened workspace.");

                StatusText.Text = "Building C# project for debugging…";
                var buildInfo = new ProcessStartInfo
                {
                    FileName = "dotnet", WorkingDirectory = Path.GetDirectoryName(project)!,
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                buildInfo.ArgumentList.Add("build");
                buildInfo.ArgumentList.Add(project);
                buildInfo.ArgumentList.Add("--configuration");
                buildInfo.ArgumentList.Add("Debug");
                using (var buildProcess = new Process { StartInfo = buildInfo })
                {
                    buildProcess.Start();
                    var stdout = buildProcess.StandardOutput.ReadToEndAsync();
                    var stderr = buildProcess.StandardError.ReadToEndAsync();
                    await buildProcess.WaitForExitAsync();
                    var buildOutput = await stdout;
                    var buildError = await stderr;
                    foreach (var line in (buildOutput + Environment.NewLine + buildError).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                        AppendTerminal(line);
                    if (buildProcess.ExitCode != 0) throw new InvalidOperationException("The C# Debug build failed. Fix the build errors shown in the terminal first.");
                }

                var projectDocument = XDocument.Load(project);
                var assemblyName = projectDocument.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value;
                if (string.IsNullOrWhiteSpace(assemblyName)) assemblyName = Path.GetFileNameWithoutExtension(project);
                var projectDirectory = Path.GetDirectoryName(project)!;
                var debugDll = Directory.EnumerateFiles(projectDirectory, assemblyName + ".dll", SearchOption.AllDirectories)
                    .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                && !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (debugDll is null) throw new FileNotFoundException("The C# Debug DLL could not be found after a successful build.");

                var netcoredbg = FindTool("netcoredbg.exe");
                if (netcoredbg is null) throw new FileNotFoundException("netcoredbg is not installed. Use Extensions → Install / Update.");
                var launch = new
                {
                    name = "Launch .NET",
                    type = "coreclr",
                    request = "launch",
                    program = debugDll,
                    cwd = projectDirectory,
                    stopAtEntry = false,
                    justMyCode = true,
                    console = "internalConsole"
                };
                StartDebugAdapter(netcoredbg, new[] { "--interpreter=vscode" }, projectDirectory, "coreclr", launch, ".NET", extension == ".cs" ? _currentFile : null);
                return;
            }

            MessageBox.Show("The debugger supports Python, JavaScript/TypeScript and .NET project files.", "Unsupported file");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the debugger.\n\n" + ex.Message, "Debugger");
            StatusText.Text = "Debugger failed to start";
        }
    }

    private void StartDebugAdapter(string executable, IEnumerable<string> arguments, string workingDirectory,
        string adapterId, object launchConfiguration, string label, string? sourcePath)
    {
        _debugAdapter?.Dispose();
        var session = new StdioProtocolProcess(executable, arguments, workingDirectory);
        _debugAdapter = session;
        session.MessageReceived += json => PostProtocolMessage("dapMessage", json);
        session.DiagnosticReceived += line => Dispatcher.BeginInvoke(() => AppendTerminal("[DAP] " + line));
        session.Exited += () => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_debugAdapter, session)) _debugAdapter = null;
            StatusText.Text = label + " debug adapter stopped";
        });
        var target = new { adapterId, launch = launchConfiguration, sourcePath };
        _ = MonacoEditor.ExecuteScriptAsync("window.studioStartDap(" + JsonSerializer.Serialize(target) + ");");
        WorkspaceTabs.SelectedItem = MonacoTab;
        StatusText.Text = "Starting " + label + " DAP session…";
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
                if (operation == "Language tooling install") RefreshLanguageServerStatus();
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
            var editorReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MonacoEditor.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var document = JsonDocument.Parse(e.WebMessageAsJson);
                    var message = document.RootElement;
                    var type = message.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    if (type == "text")
                    {
                        if (message.TryGetProperty("text", out var value))
                        {
                            _monacoText = value.GetString() ?? string.Empty;
                            _isDirty = _currentFile is not null;
                            if (_currentDocument is not null)
                            {
                                _currentDocument.Content = _monacoText;
                                _currentDocument.IsDirty = _isDirty;
                            }
                            if (_isDirty) CurrentFileText.Text = _currentDocument?.DisplayName ?? (Path.GetFileName(_currentFile) + " •");
                        }
                    }
                    else if (type == "lsp" && message.TryGetProperty("payload", out var lspPayload))
                    {
                        if (_languageServer is { IsRunning: true }) _ = SendProtocolMessageAsync(_languageServer, lspPayload.GetRawText(), "Language server");
                    }
                    else if (type == "dap" && message.TryGetProperty("payload", out var dapPayload))
                    {
                        if (_debugAdapter is { IsRunning: true }) _ = SendProtocolMessageAsync(_debugAdapter, dapPayload.GetRawText(), "Debug adapter");
                    }
                    else if (type == "editorReady") editorReady.TrySetResult(true);
                    else if (type == "editorStatus" && message.TryGetProperty("text", out var status))
                        Dispatcher.BeginInvoke(() => StatusText.Text = status.GetString() ?? "Editor ready");
                }
                catch (Exception ex) { AppendTerminal("Web editor bridge: " + ex.Message); }
            };
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
            var htmlPath = Path.Combine(assetsPath, "monaco.html");
            var loaderPath = Path.Combine(assetsPath, "monaco", "vs", "loader.js");
            if (!File.Exists(htmlPath)) throw new FileNotFoundException("Monaco host page was not published.", htmlPath);
            if (!File.Exists(loaderPath)) throw new FileNotFoundException("Bundled Monaco assets are missing. Rebuild with Node.js/npm and scripts/prepare-monaco.mjs.", loaderPath);
            MonacoEditor.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "studio.local", assetsPath,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MonacoEditor.CoreWebView2.NavigationCompleted += (_, args) => navigation.TrySetResult(args.IsSuccess);
            MonacoEditor.Source = new Uri("https://studio.local/monaco.html", UriKind.Absolute);
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Monaco page failed to load.");
            await editorReady.Task.WaitAsync(TimeSpan.FromSeconds(60));
            _monacoReady = true;
            await SetMonacoTextAsync(_monacoText, _currentFile);
        }
        catch (Exception ex) { StatusText.Text = "Monaco initialization failed: " + ex.Message; }
    }

    private async Task SetMonacoTextAsync(string text, string? path)
    {
        if (!_monacoReady) return;
        var language = Path.GetExtension(path ?? "").ToLowerInvariant() switch
        {
            ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" => "javascript",
            ".py" => "python", ".cs" => "csharp", ".csproj" => "xml",
            ".json" => "json", ".xml" or ".xaml" => "xml", _ => "plaintext"
        };
        var uri = path is null ? "untitled:studio-new-file" : new Uri(Path.GetFullPath(path)).AbsoluteUri;
        _documentUri = uri;
        await MonacoEditor.ExecuteScriptAsync("window.studioSetDocument(" +
            JsonSerializer.Serialize(text) + "," + JsonSerializer.Serialize(language) + "," + JsonSerializer.Serialize(uri) + ");");
        await StartLanguageServerForFileAsync(path, language);
    }

    private async Task StartLanguageServerForFileAsync(string? path, string language)
    {
        var serverId = language switch { "python" => "pyright", "csharp" => "csharp-ls", _ => null };
        if (serverId is null)
        {
            _languageServer?.Dispose(); _languageServer = null; _languageServerId = null;
            if (_monacoReady) _ = MonacoEditor.ExecuteScriptAsync("window.studioDisconnectLsp();");
            return;
        }
        if (path is null)
        {
            _languageServer?.Dispose(); _languageServer = null; _languageServerId = null;
            if (_monacoReady) _ = MonacoEditor.ExecuteScriptAsync("window.studioDisconnectLsp();");
            return;
        }
        var root = _projectRoot ?? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        if (_languageServer is { IsRunning: true } && _languageServerId == serverId)
        {
            return;
        }

        _languageServer?.Dispose(); _languageServer = null; _languageServerId = null;
        if (_monacoReady) await MonacoEditor.ExecuteScriptAsync("window.studioDisconnectLsp();");
        var executable = serverId == "pyright" ? FindTool("pyright-langserver.cmd", "pyright-langserver.exe", "pyright-langserver")
                                               : FindTool("roslyn-language-server.exe", "roslyn-language-server");
        if (executable is null)
        {
            if (_monacoReady) await MonacoEditor.ExecuteScriptAsync("window.studioDisconnectLsp();");
            LanguageServerStatus.Text = serverId == "pyright"
                ? "Pyright is not installed. Click Install / Update to add it (Node.js + npm required)."
                : "Microsoft Roslyn language server is not installed. Click Install / Update to add it (.NET 10 SDK required).";
            return;
        }

        try
        {
            var arguments = serverId == "pyright" ? new[] { "--stdio" } : new[] { "--stdio", "--autoLoadProjects" };
            var processExecutable = executable;
            if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                processExecutable = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                arguments = new[] { "/d", "/s", "/c", "\"" + executable + "\" --stdio" };
            }
            var session = new StdioProtocolProcess(processExecutable, arguments, root);
            _languageServer = session; _languageServerId = serverId;
            session.MessageReceived += json => PostProtocolMessage("lspMessage", json);
            session.DiagnosticReceived += line => Dispatcher.BeginInvoke(() => { AppendTerminal("[LSP] " + line); });
            session.Exited += () => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_languageServer, session)) { _languageServer = null; _languageServerId = null; }
                LanguageServerStatus.Text = serverId + " language server stopped.";
            });
            var connection = new
            {
                language,
                uri = _documentUri,
                rootUri = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar).AbsoluteUri
            };
            await MonacoEditor.ExecuteScriptAsync("window.studioConnectLsp(" + JsonSerializer.Serialize(connection) + ");");
            LanguageServerStatus.Text = (serverId == "pyright" ? "Pyright" : "Microsoft Roslyn language server") + " language server starting…";
        }
        catch (Exception ex)
        {
            LanguageServerStatus.Text = "Could not start " + serverId + ": " + ex.Message;
            AppendTerminal("[LSP] " + ex.Message);
        }
    }

    private string? FindTool(params string[] names)
    {
        var userTools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Studio.exe", "tools");
        foreach (var name in names)
        {
            var marker = name switch
            {
                "dapDebugServer.js" => Path.Combine(userTools, "js-debug", "adapter-path.txt"),
                "netcoredbg.exe" => Path.Combine(userTools, "netcoredbg", "adapter-path.txt"),
                _ => null
            };
            if (marker is not null && File.Exists(marker))
            {
                var recordedPath = File.ReadAllText(marker).Trim();
                if (File.Exists(recordedPath)) return recordedPath;
            }
        }

        var directories = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"));
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private async Task SendProtocolMessageAsync(StdioProtocolProcess session, string json, string label)
    {
        try { await session.SendAsync(json); }
        catch (Exception ex) { Dispatcher.BeginInvoke(() => AppendTerminal("[" + label + "] " + ex.Message)); }
    }

    private void PostProtocolMessage(string type, string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MonacoEditor.CoreWebView2 is null) return;
            try { MonacoEditor.CoreWebView2.PostWebMessageAsJson("{\"type\":\"" + type + "\",\"payload\":" + json + "}"); }
            catch (Exception ex) { AppendTerminal("Editor protocol event: " + ex.Message); }
        });
    }

    private void RefreshLanguageServerStatus()
    {
        var pyright = FindTool("pyright-langserver.cmd", "pyright-langserver.exe", "pyright-langserver") is not null;
        var csharp = FindTool("roslyn-language-server.exe", "roslyn-language-server") is not null;
        var jsDebug = FindTool("dapDebugServer.js") is not null;
        var netDebug = FindTool("netcoredbg.exe") is not null;
        LanguageServerStatus.Text = $"Pyright: {(pyright ? "installed" : "not installed")} · Roslyn LSP: {(csharp ? "installed" : "not installed")} · JavaScript DAP: {(jsDebug ? "installed" : "not installed")} · .NET DAP: {(netDebug ? "installed" : "not installed")} · Python DAP: debugpy";
    }

    private void InstallLanguageTools_Click(object sender, RoutedEventArgs e)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Assets", "install-language-tools.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show("The language-tool installer was not included in this build.", "Tool installer");
            return;
        }
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe", UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(_projectRoot) ? _projectRoot! : Environment.CurrentDirectory
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        StartToolProcess(info, "Language tooling install");
        WorkspaceTabs.SelectedItem = ExtensionsTab;
    }

    private async void OpenMonaco_Click(object sender, RoutedEventArgs e)
    {
        if (!_monacoReady) { MessageBox.Show("Monaco is still initializing. It is bundled locally and does not require internet access.", "Monaco Editor"); return; }
        if (WorkspaceTabs.SelectedItem == EditorTab && _wpfEditorChanged)
            _monacoText = EditorText.Text;
        else if (_currentDocument is not null)
            _monacoText = _currentDocument.Content;
        else
            _monacoText = EditorText.Text;
        _wpfEditorChanged = false;
        await SetMonacoTextAsync(_monacoText, _currentFile);
        WorkspaceTabs.SelectedItem = MonacoTab;
    }

    private void NativePowerShell_Click(object sender, RoutedEventArgs e) { ConPtyTerminal.StartupCommandLine = "pwsh.exe"; ConPtyTerminal.RestartTerm(); }
    private void NativeCommandPrompt_Click(object sender, RoutedEventArgs e) { ConPtyTerminal.StartupCommandLine = "cmd.exe"; ConPtyTerminal.RestartTerm(); }
    private void NativeRestart_Click(object sender, RoutedEventArgs e) => ConPtyTerminal.RestartTerm();

    private void ShowEditor_Click(object sender, RoutedEventArgs e)
    {
        CaptureActiveDocument();
        _isLoadingEditor = true;
        EditorText.Text = _currentDocument?.Content ?? _monacoText;
        _wpfEditorChanged = false;
        _isLoadingEditor = false;
        WorkspaceTabs.SelectedItem = EditorTab;
    }
    private void Show3D_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = ModelerTab;
    private void ShowExtensions_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = ExtensionsTab;
    private void ShowSearchGit_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedItem = SearchGitTab;
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
    private void ModelViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _modelViewportMouseDown = e.GetPosition(ModelViewport);
    }

    private void ModelViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var released = e.GetPosition(ModelViewport);
        if ((released - _modelViewportMouseDown).Length > 5) return; // preserve orbit drag
        var selected = HitTestScene(released);
        if (selected is not null) SceneList.SelectedItem = selected.Name;
    }

    private SceneObject? HitTestScene(Point screenPoint)
    {
        if (ModelViewport.ActualWidth <= 0 || ModelViewport.ActualHeight <= 0 ||
            ModelViewport.Camera is not PerspectiveCamera camera) return null;

        var forward = camera.LookDirection;
        if (forward.Length < 0.000001) return null;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        if (right.Length < 0.000001) return null;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();

        var normalizedX = (screenPoint.X / ModelViewport.ActualWidth - 0.5) * 2.0;
        var normalizedY = (0.5 - screenPoint.Y / ModelViewport.ActualHeight) * 2.0;
        var tangent = Math.Tan(camera.FieldOfView * Math.PI / 360.0);
        var aspect = ModelViewport.ActualWidth / ModelViewport.ActualHeight;
        var direction = forward + right * (normalizedX * tangent * aspect) + up * (normalizedY * tangent);
        if (direction.Length < 0.000001) return null;
        direction.Normalize();

        SceneObject? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        foreach (var item in _scene)
        {
            var indices = item.Mesh.TriangleIndices;
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var a = item.Model.Transform.Transform(item.Mesh.Positions[indices[i]]);
                var b = item.Model.Transform.Transform(item.Mesh.Positions[indices[i + 1]]);
                var c = item.Model.Transform.Transform(item.Mesh.Positions[indices[i + 2]]);
                if (RayIntersectsTriangle(camera.Position, direction, a, b, c, out var distance) &&
                    distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = item;
                }
            }
        }
        return nearest;
    }

    private static bool RayIntersectsTriangle(Point3D origin, Vector3D direction,
        Point3D a, Point3D b, Point3D c, out double distance)
    {
        const double epsilon = 1e-8;
        var edge1 = b - a;
        var edge2 = c - a;
        var h = Vector3D.CrossProduct(direction, edge2);
        var determinant = Vector3D.DotProduct(edge1, h);
        if (Math.Abs(determinant) < epsilon) { distance = 0; return false; }
        var inverse = 1.0 / determinant;
        var s = origin - a;
        var u = inverse * Vector3D.DotProduct(s, h);
        if (u < 0 || u > 1) { distance = 0; return false; }
        var q = Vector3D.CrossProduct(s, edge1);
        var v = inverse * Vector3D.DotProduct(direction, q);
        if (v < 0 || u + v > 1) { distance = 0; return false; }
        distance = inverse * Vector3D.DotProduct(edge2, q);
        return distance > epsilon;
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
    private static Material CreateMaterial(string colorHex, double opacity = 1, double specularPower = 24)
    {
        var color = (Color)(ColorConverter.ConvertFromString(colorHex) ?? Colors.SlateGray);
        color.A = (byte)Math.Clamp((int)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), 0, 255);
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(225, 232, 255)), Math.Clamp(specularPower, 1, 128)));
        return group;
    }
    private static void UpdateObjectMaterial(SceneObject item)
    {
        var material = CreateMaterial(item.ColorHex, item.Opacity, item.SpecularPower);
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
        SpecularPowerInput.Text = item.SpecularPower.ToString(CultureInfo.InvariantCulture);
        OpacityInput.Text = item.Opacity.ToString(CultureInfo.InvariantCulture);
        RefreshVertexRows(item);
    }
    private void ApplyTransform_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select an object in the scene hierarchy first."); return; }
        if (!TryNumber(ObjectX.Text, out var x) || !TryNumber(ObjectY.Text, out var y) ||
            !TryNumber(ObjectZ.Text, out var z) || !TryNumber(ScaleX.Text, out var sx) ||
            !TryNumber(ScaleY.Text, out var sy) || !TryNumber(ScaleZ.Text, out var sz) ||
            !TryNumber(RotationX.Text, out var rotationX) || !TryNumber(RotationY.Text, out var rotationY) || !TryNumber(RotationZ.Text, out var rotationZ) ||
            ColorConverter.ConvertFromString(ObjectColor.Text) is not Color color)
        {
            MessageBox.Show("Enter valid numeric transform values.", "Invalid transform"); return;
        }
        item.Name = string.IsNullOrWhiteSpace(ObjectNameInput.Text) ? item.Name : ObjectNameInput.Text.Trim();
        item.X = x; item.Y = y; item.Z = z; item.ScaleX = sx; item.ScaleY = sy; item.ScaleZ = sz;
        if (!TryNumber(SpecularPowerInput.Text, out var specularPower) || !TryNumber(OpacityInput.Text, out var opacity))
        {
            MessageBox.Show("Enter a valid specular value and opacity between 0 and 1.", "Invalid material"); return;
        }
        item.RotationX = rotationX; item.RotationY = rotationY; item.RotationZ = rotationZ; item.ColorHex = color.ToString();
        item.SpecularPower = Math.Clamp(specularPower, 1, 128); item.Opacity = Math.Clamp(opacity, 0, 1);
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
        item.Model.Geometry = item.Mesh; RefreshVertexRows(item);
        StatusText.Text = $"{item.Name}: subdivided to {indices.Count / 3} triangles";
    }

    private void RefreshVertexRows(SceneObject item)
    {
        _vertexRows.Clear();
        for (var i = 0; i < item.Mesh.Positions.Count; i++)
        {
            var p = item.Mesh.Positions[i];
            _vertexRows.Add(new VertexEditRow { Index = i, X = p.X, Y = p.Y, Z = p.Z });
        }
        VertexGrid.ItemsSource = _vertexRows;
    }

    private void ApplyVertexEdits_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) return;
        VertexGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        VertexGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var positions = item.Mesh.Positions.ToList();
        foreach (var row in _vertexRows)
        {
            if (row.Index < 0 || row.Index >= positions.Count) continue;
            positions[row.Index] = new Point3D(row.X, row.Y, row.Z);
        }
        item.Mesh = new MeshGeometry3D { Positions = new Point3DCollection(positions), TriangleIndices = new Int32Collection(item.Mesh.TriangleIndices) };
        item.Model.Geometry = item.Mesh;
        StatusText.Text = $"Updated {positions.Count} vertices on {item.Name}";
    }

    private void FlipNormals_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) return;
        var indices = new Int32Collection(item.Mesh.TriangleIndices);
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var temp = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = temp;
        }
        item.Mesh = new MeshGeometry3D { Positions = new Point3DCollection(item.Mesh.Positions), TriangleIndices = indices };
        item.Model.Geometry = item.Mesh;
        StatusText.Text = "Face winding reversed · normals flipped";
    }

    private void SmoothMesh_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) return;
        var source = item.Mesh.Positions.ToArray();
        var neighbors = Enumerable.Range(0, source.Length).Select(_ => new HashSet<int>()).ToArray();
        var triangles = item.Mesh.TriangleIndices;
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            var a = triangles[i]; var b = triangles[i + 1]; var c = triangles[i + 2];
            neighbors[a].Add(b); neighbors[a].Add(c);
            neighbors[b].Add(a); neighbors[b].Add(c);
            neighbors[c].Add(a); neighbors[c].Add(b);
        }
        var smoothed = new Point3DCollection();
        for (var i = 0; i < source.Length; i++)
        {
            if (neighbors[i].Count == 0) { smoothed.Add(source[i]); continue; }
            var adjacent = neighbors[i].Select(index => source[index]).ToArray();
            var average = new Point3D(adjacent.Average(p => p.X), adjacent.Average(p => p.Y), adjacent.Average(p => p.Z));
            smoothed.Add(new Point3D(source[i].X * 0.75 + average.X * 0.25,
                source[i].Y * 0.75 + average.Y * 0.25, source[i].Z * 0.75 + average.Z * 0.25));
        }
        item.Mesh = new MeshGeometry3D { Positions = smoothed, TriangleIndices = new Int32Collection(triangles) };
        item.Model.Geometry = item.Mesh;
        RefreshVertexRows(item);
        StatusText.Text = "Applied one Laplacian smoothing pass";
    }

    private void ImportMesh_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "3D mesh files (*.obj;*.stl)|*.obj;*.stl|Wavefront OBJ (*.obj)|*.obj|STL (*.stl)|*.stl" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            var mesh = extension == ".obj" ? ImportObj(dialog.FileName) : ImportStl(dialog.FileName);
            if (mesh.Positions.Count == 0 || mesh.TriangleIndices.Count < 3)
                throw new InvalidDataException("The selected file did not contain any triangle mesh geometry.");
            AddSceneObjectFromMesh(Path.GetFileNameWithoutExtension(dialog.FileName), mesh);
            StatusText.Text = $"Imported {Path.GetFileName(dialog.FileName)} · {mesh.Positions.Count} vertices / {mesh.TriangleIndices.Count / 3} triangles";
        }
        catch (Exception ex) { MessageBox.Show($"Could not import mesh:\n{ex.Message}", "3D import failed"); }
    }

    private static MeshGeometry3D ImportObj(string path)
    {
        var mesh = new MeshGeometry3D();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length >= 4 && TryParseFloat(values[1], out var x) && TryParseFloat(values[2], out var y) && TryParseFloat(values[3], out var z))
                    mesh.Positions.Add(new Point3D(x, y, z));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                var face = new List<int>();
                foreach (var token in tokens)
                {
                    var value = token.Split('/')[0];
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
                        throw new InvalidDataException("OBJ contains an invalid face index.");
                    var resolved = index > 0 ? index - 1 : mesh.Positions.Count + index;
                    if (resolved < 0 || resolved >= mesh.Positions.Count) throw new InvalidDataException("OBJ face index is out of range.");
                    face.Add(resolved);
                }
                for (var i = 1; i + 1 < face.Count; i++)
                {
                    mesh.TriangleIndices.Add(face[0]); mesh.TriangleIndices.Add(face[i]); mesh.TriangleIndices.Add(face[i + 1]);
                }
            }
        }
        return mesh;
    }

    private static MeshGeometry3D ImportStl(string path)
    {
        var data = File.ReadAllBytes(path);
        var mesh = new MeshGeometry3D();
        if (data.Length >= 84)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80, 4));
            if (count > 0 && 84L + 50L * count == data.Length)
            {
                var offset = 84;
                for (var triangle = 0; triangle < count; triangle++)
                {
                    offset += 12;
                    for (var vertex = 0; vertex < 3; vertex++)
                    {
                        var x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))); offset += 4;
                        var y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))); offset += 4;
                        var z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))); offset += 4;
                        mesh.TriangleIndices.Add(mesh.Positions.Count); mesh.Positions.Add(new Point3D(x, y, z));
                    }
                    offset += 2;
                }
                return mesh;
            }
        }
        var vertices = Regex.Matches(File.ReadAllText(path), @"vertex\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)", RegexOptions.IgnoreCase);
        foreach (Match vertex in vertices)
        {
            if (!TryParseFloat(vertex.Groups[1].Value, out var x) || !TryParseFloat(vertex.Groups[2].Value, out var y) ||
                !TryParseFloat(vertex.Groups[3].Value, out var z)) throw new InvalidDataException("ASCII STL contains an invalid vertex.");
            mesh.TriangleIndices.Add(mesh.Positions.Count); mesh.Positions.Add(new Point3D(x, y, z));
        }
        if (mesh.Positions.Count % 3 != 0) throw new InvalidDataException("ASCII STL does not contain complete facets.");
        return mesh;
    }

    private static bool TryParseFloat(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private void AddSceneObjectFromMesh(string name, MeshGeometry3D mesh)
    {
        var material = CreateMaterial("#718EFF");
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        var item = new SceneObject { Name = name, Shape = "Custom", Mesh = mesh, Model = model, ColorHex = "#718EFF" };
        UpdateObjectTransform(item); _scene.Add(item); _world.Children.Add(model); RefreshSceneList(item.Name);
        RefreshVertexRows(item);
    }

    private void AddKeyframe_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select an object before adding a keyframe."); return; }
        var time = TimelineSlider.Value;
        item.Keyframes.RemoveAll(k => Math.Abs(k.Time - time) < 0.0001);
        item.Keyframes.Add(new SceneKeyframe
        {
            Time = time, X = item.X, Y = item.Y, Z = item.Z,
            ScaleX = item.ScaleX, ScaleY = item.ScaleY, ScaleZ = item.ScaleZ,
            RotationX = item.RotationX, RotationY = item.RotationY, RotationZ = item.RotationZ
        });
        item.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        StatusText.Text = "Keyframe added for " + item.Name + " at " + time.ToString("0.00", CultureInfo.InvariantCulture) + "s";
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SceneList is null) return;
        TimelineTimeText.Text = TimelineSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is not null) ApplyAnimationAtTime(item, TimelineSlider.Value);
    }

    private static void ApplyAnimationAtTime(SceneObject item, double time)
    {
        if (item.Keyframes.Count == 0) return;
        var before = item.Keyframes.LastOrDefault(k => k.Time <= time) ?? item.Keyframes[0];
        var after = item.Keyframes.FirstOrDefault(k => k.Time >= time) ?? item.Keyframes[^1];
        var amount = after.Time <= before.Time ? 0 : Math.Clamp((time - before.Time) / (after.Time - before.Time), 0, 1);
        static double L(double a, double b, double t) => a + (b - a) * t;
        item.X = L(before.X, after.X, amount); item.Y = L(before.Y, after.Y, amount); item.Z = L(before.Z, after.Z, amount);
        item.ScaleX = L(before.ScaleX, after.ScaleX, amount); item.ScaleY = L(before.ScaleY, after.ScaleY, amount); item.ScaleZ = L(before.ScaleZ, after.ScaleZ, amount);
        item.RotationX = L(before.RotationX, after.RotationX, amount); item.RotationY = L(before.RotationY, after.RotationY, amount); item.RotationZ = L(before.RotationZ, after.RotationZ, amount);
        UpdateObjectTransform(item);
    }

    private void PlayTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (!_scene.Any(x => x.Keyframes.Count > 0)) { MessageBox.Show("Add at least one keyframe to the scene."); return; }
        if (TimelineSlider.Value >= TimelineSlider.Maximum) TimelineSlider.Value = TimelineSlider.Minimum;
        _animationTimer.Start(); StatusText.Text = "Timeline playing";
    }
    private void StopTimeline_Click(object sender, RoutedEventArgs e)
    {
        _animationTimer.Stop(); StatusText.Text = "Timeline paused";
    }
    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (TimelineSlider.Value + 1.0 / 30.0 >= TimelineSlider.Maximum)
        {
            TimelineSlider.Value = TimelineSlider.Maximum; _animationTimer.Stop(); StatusText.Text = "Timeline finished"; return;
        }
        TimelineSlider.Value += 1.0 / 30.0;
    }

    private void InsetTriangle_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select a mesh first."); return; }
        if (!int.TryParse(TopologyTriangleIndex.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var triangle) ||
            !TryNumber(InsetAmountInput.Text, out var fraction) || fraction <= 0 || fraction > 0.45)
        {
            MessageBox.Show("Enter a valid triangle index and inset fraction between 0 and 0.45.", "Inset face");
            return;
        }
        var indices = item.Mesh.TriangleIndices;
        if (triangle < 0 || triangle >= indices.Count / 3)
        {
            MessageBox.Show("Triangle index is outside the mesh.", "Inset face");
            return;
        }
        var offset = triangle * 3;
        var aIndex = indices[offset]; var bIndex = indices[offset + 1]; var cIndex = indices[offset + 2];
        var positions = item.Mesh.Positions.ToList();
        var a = positions[aIndex]; var b = positions[bIndex]; var c = positions[cIndex];
        var center = new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
        var amount = 1.0 - fraction;
        var insetA = positions.Count; positions.Add(new Point3D(center.X + (a.X - center.X) * amount, center.Y + (a.Y - center.Y) * amount, center.Z + (a.Z - center.Z) * amount));
        var insetB = positions.Count; positions.Add(new Point3D(center.X + (b.X - center.X) * amount, center.Y + (b.Y - center.Y) * amount, center.Z + (b.Z - center.Z) * amount));
        var insetC = positions.Count; positions.Add(new Point3D(center.X + (c.X - center.X) * amount, center.Y + (c.Y - center.Y) * amount, center.Z + (c.Z - center.Z) * amount));
        var result = new Int32Collection();
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            if (i != offset) { result.Add(indices[i]); result.Add(indices[i + 1]); result.Add(indices[i + 2]); continue; }
            result.Add(aIndex); result.Add(bIndex); result.Add(insetB);
            result.Add(aIndex); result.Add(insetB); result.Add(insetA);
            result.Add(bIndex); result.Add(cIndex); result.Add(insetC);
            result.Add(bIndex); result.Add(insetC); result.Add(insetB);
            result.Add(cIndex); result.Add(aIndex); result.Add(insetA);
            result.Add(cIndex); result.Add(insetA); result.Add(insetC);
            result.Add(insetA); result.Add(insetB); result.Add(insetC);
        }
        item.Mesh = new MeshGeometry3D { Positions = new Point3DCollection(positions), TriangleIndices = result };
        item.Model.Geometry = item.Mesh;
        RefreshVertexRows(item);
        StatusText.Text = item.Name + ": inset triangle " + triangle;
    }

    private void SplitEdge_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select a mesh first."); return; }
        if (!int.TryParse(TopologyTriangleIndex.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var triangle) ||
            !int.TryParse(SplitEdgeIndexInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var edge) || edge is < 0 or > 2)
        {
            MessageBox.Show("Enter a valid triangle index and edge 0, 1 or 2.", "Split edge");
            return;
        }
        var indices = item.Mesh.TriangleIndices;
        if (triangle < 0 || triangle >= indices.Count / 3)
        {
            MessageBox.Show("Triangle index is outside the mesh.", "Split edge");
            return;
        }
        var offset = triangle * 3;
        var edgeA = indices[offset + edge];
        var edgeB = indices[offset + ((edge + 1) % 3)];
        var sharedMidpoint = item.Mesh.Positions.Count;
        var pa = item.Mesh.Positions[edgeA]; var pb = item.Mesh.Positions[edgeB];
        var midpoint = new Point3D((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2, (pa.Z + pb.Z) / 2);
        var positions = new Point3DCollection(item.Mesh.Positions) { midpoint };
        var result = new Int32Collection();
        var splitCount = 0;
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var vertices = new[] { indices[i], indices[i + 1], indices[i + 2] };
            var split = false;
            for (var corner = 0; corner < 3; corner++)
            {
                var u = vertices[corner];
                var v = vertices[(corner + 1) % 3];
                var w = vertices[(corner + 2) % 3];
                if ((u == edgeA && v == edgeB) || (u == edgeB && v == edgeA))
                {
                    result.Add(u); result.Add(sharedMidpoint); result.Add(w);
                    result.Add(sharedMidpoint); result.Add(v); result.Add(w);
                    split = true; splitCount++; break;
                }
            }
            if (!split) { result.Add(vertices[0]); result.Add(vertices[1]); result.Add(vertices[2]); }
        }
        item.Mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = result };
        item.Model.Geometry = item.Mesh;
        RefreshVertexRows(item);
        StatusText.Text = item.Name + ": split edge shared by " + splitCount + " triangle(s)";
    }

    private void ExtrudeFace_Click(object sender, RoutedEventArgs e)
    {
        var item = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (item is null) { MessageBox.Show("Select an object first."); return; }
        if (!int.TryParse(ExtrudeTriangleIndex.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var triangle) ||
            !TryNumber(ExtrudeDistanceInput.Text, out var distance) || Math.Abs(distance) < 0.000001)
        {
            MessageBox.Show("Enter a valid zero-based triangle index and a non-zero extrusion distance.", "Extrude face");
            return;
        }
        var sourceIndices = item.Mesh.TriangleIndices;
        var triangleCount = sourceIndices.Count / 3;
        if (triangle < 0 || triangle >= triangleCount)
        {
            MessageBox.Show("Triangle index is outside the mesh. Triangle indices run from 0 to " + Math.Max(0, triangleCount - 1) + ".", "Extrude face");
            return;
        }

        var start = triangle * 3;
        var aIndex = sourceIndices[start]; var bIndex = sourceIndices[start + 1]; var cIndex = sourceIndices[start + 2];
        var positions = item.Mesh.Positions.ToList();
        var a = positions[aIndex]; var b = positions[bIndex]; var c = positions[cIndex];
        var normal = Vector3D.CrossProduct(b - a, c - a);
        if (normal.Length < 0.000001) { MessageBox.Show("The selected triangle is degenerate and cannot be extruded."); return; }
        normal.Normalize();
        var offset = normal * distance;

        var aTop = positions.Count; positions.Add(a + offset);
        var bTop = positions.Count; positions.Add(b + offset);
        var cTop = positions.Count; positions.Add(c + offset);
        var triangles = new Int32Collection();
        // Keep the original triangle as the base; the translated cap and side faces form the extrusion.
        for (var i = 0; i + 2 < sourceIndices.Count; i += 3)
        {
            triangles.Add(sourceIndices[i]); triangles.Add(sourceIndices[i + 1]); triangles.Add(sourceIndices[i + 2]);
        }

        // New cap and two triangles for each of the three side quads.
        triangles.Add(aTop); triangles.Add(bTop); triangles.Add(cTop);
        triangles.Add(aIndex); triangles.Add(bIndex); triangles.Add(bTop);
        triangles.Add(aIndex); triangles.Add(bTop); triangles.Add(aTop);
        triangles.Add(bIndex); triangles.Add(cIndex); triangles.Add(cTop);
        triangles.Add(bIndex); triangles.Add(cTop); triangles.Add(bTop);
        triangles.Add(cIndex); triangles.Add(aIndex); triangles.Add(aTop);
        triangles.Add(cIndex); triangles.Add(aTop); triangles.Add(cTop);

        item.Mesh = new MeshGeometry3D { Positions = new Point3DCollection(positions), TriangleIndices = triangles };
        item.Model.Geometry = item.Mesh;
        RefreshVertexRows(item);
        StatusText.Text = item.Name + ": extruded triangle " + triangle + " by " + distance.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void DuplicateObject_Click(object sender, RoutedEventArgs e)
    {
        var source = _scene.FirstOrDefault(x => x.Name == SceneList.SelectedItem as string);
        if (source is null) return;
        var mesh = new MeshGeometry3D { Positions = new Point3DCollection(source.Mesh.Positions), TriangleIndices = new Int32Collection(source.Mesh.TriangleIndices) };
        var material = CreateMaterial(source.ColorHex, source.Opacity, source.SpecularPower);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        var copy = new SceneObject
        {
            Name = source.Name + " Copy", Shape = source.Shape, Mesh = mesh, Model = model,
            X = source.X + 1, Y = source.Y, Z = source.Z, ScaleX = source.ScaleX, ScaleY = source.ScaleY,
            ScaleZ = source.ScaleZ, RotationX = source.RotationX, RotationY = source.RotationY, RotationZ = source.RotationZ,
            ColorHex = source.ColorHex, SpecularPower = source.SpecularPower, Opacity = source.Opacity,
            Keyframes = source.Keyframes.Select(k => k.Clone()).ToList()
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
            FormatVersion = 3,
            Objects = _scene.Select(x => new SceneObjectFileData
            {
                Name = x.Name, Shape = x.Shape, X = x.X, Y = x.Y, Z = x.Z,
                ScaleX = x.ScaleX, ScaleY = x.ScaleY, ScaleZ = x.ScaleZ,
                RotationX = x.RotationX, RotationY = x.RotationY, RotationZ = x.RotationZ,
                ColorHex = x.ColorHex, SpecularPower = x.SpecularPower, Opacity = x.Opacity,
                Keyframes = x.Keyframes.Select(k => k.Clone()).ToList(),
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
            if (doc is null || doc.FormatVersion is < 1 or > 3) throw new InvalidDataException("Unsupported or invalid Studio 3D scene.");
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
                var material = CreateMaterial(colorHex, saved.Opacity, saved.SpecularPower);
                var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
                var item = new SceneObject
                {
                    Name = saved.Name, Shape = saved.Shape, Mesh = mesh, Model = model,
                    X = saved.X, Y = saved.Y, Z = saved.Z, ScaleX = saved.ScaleX,
                    ScaleY = saved.ScaleY, ScaleZ = saved.ScaleZ,
                    RotationX = saved.RotationX, RotationY = saved.RotationY, RotationZ = saved.RotationZ,
                    ColorHex = colorHex, SpecularPower = saved.SpecularPower, Opacity = saved.Opacity, Keyframes = saved.Keyframes ?? []
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
        var dialog = new SaveFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj|ASCII STL (*.stl)|*.stl|glTF binary (*.glb)|*.glb", FileName = "scene.glb" };
        if (dialog.ShowDialog() != true) return;
        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (extension == ".glb") File.WriteAllBytes(dialog.FileName, ExportGlb());
        else File.WriteAllText(dialog.FileName, extension == ".stl" ? ExportStl() : ExportObj(), new UTF8Encoding(false));
        StatusText.Text = "Exported " + Path.GetFileName(dialog.FileName);
    }
    
    private byte[] ExportGlb()
    {
        using var binary = new MemoryStream();
        using var writer = new BinaryWriter(binary, Encoding.UTF8, leaveOpen: true);
        var bufferViews = new List<object>();
        var accessors = new List<object>();
        var meshes = new List<object>();
        var nodes = new List<object>();
        var materials = new List<object>();
        var nodeIndices = new List<int>();

        foreach (var item in _scene)
        {
            var indices = item.Mesh.TriangleIndices;
            var expandedVertexCount = (indices.Count / 3) * 3;
            if (expandedVertexCount == 0) continue;

            AlignBuffer(binary, writer);
            var positionOffset = checked((int)binary.Position);
            var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            var worldPositions = new List<Point3D>(expandedVertexCount);

            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                foreach (var index in new[] { indices[i], indices[i + 1], indices[i + 2] })
                {
                    var point = item.Model.Transform.Transform(item.Mesh.Positions[index]);
                    worldPositions.Add(point);
                    min[0] = Math.Min(min[0], point.X); min[1] = Math.Min(min[1], point.Y); min[2] = Math.Min(min[2], point.Z);
                    max[0] = Math.Max(max[0], point.X); max[1] = Math.Max(max[1], point.Y); max[2] = Math.Max(max[2], point.Z);
                    writer.Write((float)point.X); writer.Write((float)point.Y); writer.Write((float)point.Z);
                }
            }

            var positionLength = checked((int)binary.Position - positionOffset);
            var positionView = bufferViews.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = positionOffset, byteLength = positionLength, target = 34962 });
            var positionAccessor = accessors.Count;
            accessors.Add(new { bufferView = positionView, componentType = 5126, count = expandedVertexCount, type = "VEC3", min, max });

            AlignBuffer(binary, writer);
            var normalOffset = checked((int)binary.Position);
            for (var i = 0; i + 2 < worldPositions.Count; i += 3)
            {
                var a = worldPositions[i]; var b = worldPositions[i + 1]; var c = worldPositions[i + 2];
                var normal = Vector3D.CrossProduct(b - a, c - a);
                if (normal.Length > 0) normal.Normalize();
                for (var vertex = 0; vertex < 3; vertex++)
                {
                    writer.Write((float)normal.X); writer.Write((float)normal.Y); writer.Write((float)normal.Z);
                }
            }
            var normalLength = checked((int)binary.Position - normalOffset);
            var normalView = bufferViews.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = normalOffset, byteLength = normalLength, target = 34962 });
            var normalAccessor = accessors.Count;
            accessors.Add(new { bufferView = normalView, componentType = 5126, count = expandedVertexCount, type = "VEC3" });

            var color = (Color)(ColorConverter.ConvertFromString(item.ColorHex) ?? Colors.SlateGray);
            var materialIndex = materials.Count;
            materials.Add(new
            {
                name = item.Name + " Material",
                pbrMetallicRoughness = new
                {
                    baseColorFactor = new[] { color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0 * item.Opacity },
                    metallicFactor = 0.0,
                    roughnessFactor = Math.Clamp(1.0 - item.SpecularPower / 160.0, 0.05, 1.0)
                },
                alphaMode = item.Opacity < 1 ? "BLEND" : "OPAQUE",
                doubleSided = true
            });

            var meshIndex = meshes.Count;
            meshes.Add(new
            {
                name = item.Name,
                primitives = new[]
                {
                    new
                    {
                        attributes = new Dictionary<string, int> { ["POSITION"] = positionAccessor, ["NORMAL"] = normalAccessor },
                        material = materialIndex,
                        mode = 4
                    }
                }
            });
            nodeIndices.Add(nodes.Count);
            nodes.Add(new { name = item.Name, mesh = meshIndex });
        }

        if (meshes.Count == 0) throw new InvalidDataException("No exportable triangles exist in the scene.");
        var sceneRoot = new
        {
            asset = new { version = "2.0", generator = "Studio.exe" },
            scene = 0,
            scenes = new[] { new { nodes = nodeIndices } },
            nodes,
            meshes,
            materials,
            buffers = new[] { new { byteLength = binary.Length } },
            bufferViews,
            accessors
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(sceneRoot);
        var jsonPadding = (4 - json.Length % 4) % 4;
        var binBytes = binary.ToArray();
        var binPadding = (4 - binBytes.Length % 4) % 4;
        var totalLength = checked(12 + 8 + json.Length + jsonPadding + 8 + binBytes.Length + binPadding);

        using var output = new MemoryStream(totalLength);
        using var glb = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        glb.Write(0x46546C67); // glTF magic
        glb.Write(2);
        glb.Write(totalLength);
        glb.Write(json.Length + jsonPadding);
        glb.Write(0x4E4F534A); // JSON chunk
        glb.Write(json);
        for (var i = 0; i < jsonPadding; i++) glb.Write((byte)0x20);
        glb.Write(binBytes.Length + binPadding);
        glb.Write(0x004E4942); // BIN chunk
        glb.Write(binBytes);
        for (var i = 0; i < binPadding; i++) glb.Write((byte)0);
        return output.ToArray();
    }

    private static void AlignBuffer(Stream stream, BinaryWriter writer)
    {
        while (stream.Position % 4 != 0) writer.Write((byte)0);
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

    private async void SearchWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var root = _projectRoot;
        var query = WorkspaceSearchQuery.Text;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show("Open a project folder before searching.", "Workspace search");
            return;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            MessageBox.Show("Enter text to search for.", "Workspace search");
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var comparison = SearchCaseSensitive.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        StatusText.Text = "Searching workspace…";
        SearchResultsList.ItemsSource = null;
        try
        {
            var results = await Task.Run(() => SearchWorkspaceFiles(root, query, comparison, token), token);
            SearchResultsList.ItemsSource = results;
            StatusText.Text = $"Found {results.Count} match(es)";
        }
        catch (OperationCanceledException) { StatusText.Text = "Search cancelled"; }
        catch (Exception ex) { StatusText.Text = "Search failed: " + ex.Message; }
    }

    private static List<SearchResult> SearchWorkspaceFiles(string root, string query, StringComparison comparison, CancellationToken token)
    {
        var results = new List<SearchResult>();
        var directories = new Stack<string>();
        directories.Push(root);
        var ignoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", ".vs", "bin", "obj", "node_modules", "artifacts", ".idea", "packages" };
        while (directories.Count > 0 && results.Count < 1000)
        {
            token.ThrowIfCancellationRequested();
            var current = directories.Pop();
            IEnumerable<string> subDirectories;
            IEnumerable<string> files;
            try
            {
                subDirectories = Directory.EnumerateDirectories(current).ToArray();
                files = Directory.EnumerateFiles(current).ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var directory in subDirectories)
            {
                token.ThrowIfCancellationRequested();
                if (!ignoredDirectories.Contains(Path.GetFileName(directory))) directories.Push(directory);
            }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 2 * 1024 * 1024) continue;
                    if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".zip", ".7z", ".dll", ".exe", ".pdb", ".woff", ".woff2", ".ttf" }
                        .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNumber++;
                        var index = line.IndexOf(query, comparison);
                        if (index < 0) continue;
                        var preview = line.Trim();
                        if (preview.Length > 220) preview = preview[..220] + "…";
                        results.Add(new SearchResult(file, lineNumber, index + 1, preview));
                        if (results.Count >= 1000) break;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DecoderFallbackException) { }
                if (results.Count >= 1000) break;
            }
        }
        return results;
    }

    private async void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not SearchResult result) return;
        OpenFile(result.FilePath);
        await Task.Delay(150);
        if (_monacoReady)
        {
            await MonacoEditor.ExecuteScriptAsync("window.studioRevealPosition(" +
                result.Line.ToString(CultureInfo.InvariantCulture) + "," +
                result.Column.ToString(CultureInfo.InvariantCulture) + ");");
        }
    }

    private void RefreshGit_Click(object sender, RoutedEventArgs e) => _ = RunGitCommandAsync("status", "--short", "--branch");
    private void GitPull_Click(object sender, RoutedEventArgs e) => _ = RunGitCommandAsync("pull", "--ff-only");
    private void GitPush_Click(object sender, RoutedEventArgs e) => _ = RunGitCommandAsync("push");

    private async void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        var message = GitCommitMessage.Text.Trim();
        if (message.Length == 0) { MessageBox.Show("Enter a commit message first.", "Git commit"); return; }
        if (!await RunGitCommandAsync("add", "--all")) return;
        await RunGitCommandAsync("commit", "-m", message);
    }

    private async Task<bool> RunGitCommandAsync(params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot) || !Directory.Exists(_projectRoot))
        {
            MessageBox.Show("Open a project folder before using Git.", "Git");
            return false;
        }
        var info = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = _projectRoot,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "Never";
        try
        {
            using var process = new Process { StartInfo = info };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var command = "git " + string.Join(" ", arguments);
            GitOutput.Text += Environment.NewLine + "> " + command + Environment.NewLine + output + error;
            GitOutput.ScrollToEnd();
            StatusText.Text = process.ExitCode == 0 ? command + " completed" : command + " failed (exit " + process.ExitCode + ")";
            if (arguments.FirstOrDefault() is "commit" or "pull" or "push" or "add")
                await RefreshGitStatusOutputAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            GitOutput.Text += Environment.NewLine + "Git error: " + ex.Message;
            StatusText.Text = "Git command failed";
            return false;
        }
    }

    private async Task RefreshGitStatusOutputAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot)) return;
        var info = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = _projectRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        info.ArgumentList.Add("status"); info.ArgumentList.Add("--short"); info.ArgumentList.Add("--branch");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var process = Process.Start(info);
            if (process is null) return;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            GitOutput.Text += Environment.NewLine + "> git status --short --branch" + Environment.NewLine + output + error;
            GitOutput.ScrollToEnd();
        }
        catch (Exception ex) { GitOutput.Text += Environment.NewLine + "Git status failed: " + ex.Message; }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show($"{Title}\nWindows development workspace for JavaScript, Python, C#, Monaco, LSP/DAP integrations, a native ConPTY terminal, and a mesh-based 3D editor.\n\nIncludes multiple open Monaco documents, language-server features, Python/Node/.NET debug adapters, mesh import/edit/export, materials, and keyframe animation. See README for the remaining advanced IDE/modeling limits.",
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
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        _terminal.Dispose(); _languageServer?.Dispose(); _debugAdapter?.Dispose(); _animationTimer.Stop();
        try { if (_runProcess is { HasExited: false }) _runProcess.Kill(entireProcessTree: true); } catch { }
    }

    private sealed record EditorDiagnostic(int Line, int Column, string Message, string Severity);

    private sealed class SceneFileData
    {
        public int FormatVersion { get; set; } = 3;
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
        public double SpecularPower { get; set; } = 24;
        public double Opacity { get; set; } = 1;
        public List<SceneKeyframe>? Keyframes { get; set; }
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
        public double SpecularPower { get; set; } = 24;
        public double Opacity { get; set; } = 1;
        public List<SceneKeyframe> Keyframes { get; set; } = [];
    }

    private sealed class SceneKeyframe
    {
        public double Time { get; set; }
        public double X { get; set; } public double Y { get; set; } public double Z { get; set; }
        public double ScaleX { get; set; } = 1; public double ScaleY { get; set; } = 1; public double ScaleZ { get; set; } = 1;
        public double RotationX { get; set; } public double RotationY { get; set; } public double RotationZ { get; set; }
        public SceneKeyframe Clone() => (SceneKeyframe)MemberwiseClone();
    }
    private sealed record SearchResult(string FilePath, int Line, int Column, string Preview)
    {
        public string DisplayLabel => $"{Path.GetFileName(FilePath)}:{Line}";
    }

    private sealed class OpenDocument : INotifyPropertyChanged
    {
        private bool _isDirty;
        private string _filePath = string.Empty;
        public string FilePath
        {
            get => _filePath;
            set { if (!string.Equals(_filePath, value, StringComparison.OrdinalIgnoreCase)) { _filePath = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilePath))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName))); } }
        }
        public string Content { get; set; } = string.Empty;
        public bool IsDirty
        {
            get => _isDirty;
            set { if (_isDirty != value) { _isDirty = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName))); } }
        }
        public string DisplayName => Path.GetFileName(FilePath) + (IsDirty ? "  •" : string.Empty);
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class VertexEditRow : INotifyPropertyChanged
    {
        private double _x, _y, _z;
        public int Index { get; set; }
        public double X { get => _x; set { if (_x != value) { _x = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(X))); } } }
        public double Y { get => _y; set { if (_y != value) { _y = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Y))); } } }
        public double Z { get => _z; set { if (_z != value) { _z = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Z))); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
