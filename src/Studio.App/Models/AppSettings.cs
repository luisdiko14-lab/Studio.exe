namespace StudioApp.Models;

public sealed class AppSettings
{
    public bool SetupCompleted { get; set; }
    public string StudioName { get; set; } = "My Studio";
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#7387FF";
    public string DefaultProjectDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public bool JavaScriptEnabled { get; set; } = true;
    public bool PythonEnabled { get; set; } = true;
    public bool CSharpEnabled { get; set; } = true;
}

public sealed class ExplorerEntry
{
    public ExplorerEntry(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string DisplayName => IsDirectory ? $"▸  {Name}" : $"     {Name}";
}

public sealed class SceneObjectData
{
    public string Name { get; set; } = "Object";
    public string Shape { get; set; } = "Cube";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double ScaleZ { get; set; } = 1;
    public double RotationY { get; set; }
}

public sealed class SceneDocument
{
    public int FormatVersion { get; set; } = 1;
    public List<SceneObjectData> Objects { get; set; } = [];
}
