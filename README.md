# Studio.exe

Studio.exe is an early Windows desktop development-studio project built with C# and WPF on .NET 10. It combines a first-launch studio wizard, a project/file workspace, a persistent PowerShell or Command Prompt session, basic JavaScript/Python/C# run integrations, and a lightweight WPF 3D scene editor.

> **Project status:** this repository now contains a buildable first implementation, not the complete commercial-grade IDE described in the original product brief. Read the limitations below before relying on advanced editor, terminal, debugging, or modeling capabilities.

## Current features

- First-launch wizard for studio name, theme, accent, and default project folder. Settings persist in the user's AppData folder.
- WPF workspace with project-folder browsing, open/create/save file actions, and tabs for the editor, 3D modeler, and three language integrations.
- Real PowerShell or cmd.exe child process with streamed output and command input. Commands run as the current Windows user.
- Run JavaScript files with Node.js, Python files with Python, and C# projects with dotnet run. Build searches the opened project root for a solution or project file.
- Exactly three language integration toggles: JavaScript, Python, and C#.
- Interactive WPF 3D viewport with cube and sphere primitives, scene list, numeric transforms, duplicate/delete, JSON scene save/load, and OBJ/ASCII STL export.
- PowerShell setup/build/run scripts and a Windows GitHub Actions workflow that builds and uploads a self-contained x64 publish artifact.

## Requirements

- Windows 10 or Windows 11.
- .NET 10 SDK for local building.
- Node.js for JavaScript execution.
- Python for Python execution.
- .NET 10 SDK for C# project build/run.

## Build from source

Open PowerShell in the repository directory and run:

    Set-ExecutionPolicy -Scope Process Bypass
    .\setup.ps1
    .\build.ps1

Publish a self-contained x64 build:

    .\build.ps1 -Publish -SelfContained

The published executable is written to artifacts\Studio\Studio.exe.

Run the project with:

    .\run.ps1

Or double-click start.bat.

A GitHub Actions workflow also runs on pushes and pull requests to main. Successful push/manual runs upload an artifact named Studio.exe-win-x64.

## Current limitations

This is a practical first implementation, and several advanced items from the full prompt are not implemented yet:

- The editor is currently a WPF text editor. It does not yet provide Monaco/AvalonEdit, syntax coloring, IntelliSense, language-server diagnostics, minimap, split views, or a full command palette.
- The shell is a persistent real process using redirected standard input/output. It is not a Windows ConPTY terminal, so full-screen TUI applications, interactive console applications, terminal resizing/ANSI behavior, and some prompt types are not supported.
- The three language cards enable/disable integrations, but are not a downloadable extension marketplace. Runtime and SDK detection is basic and errors are reported by the launched process.
- Debugging, launch profiles, Git UI, project templates, global search, tab restoration, and advanced workspace customization are not implemented yet. Use the integrated shell for Git and package-manager commands.
- 3D currently supports cube and sphere primitives, simple transforms, JSON scene persistence, and OBJ/ASCII STL export. It does not yet provide orbit/pan/zoom controls, mesh editing, materials UI, lights UI, timeline/animation, GLB/glTF export, or custom mesh import.

Only features implemented in this repository are advertised as available. The remaining items can be added incrementally without replacing the working base.

## Repository structure

    Studio.sln
    src/Studio.App/
      App.xaml
      MainWindow.xaml
      MainWindow.xaml.cs
      Models/
      Services/
    setup.ps1
    build.ps1
    run.ps1
    start.bat
    .github/workflows/build.yml

## Safety

The app does not require administrator rights. It does not silently download runtimes or run setup scripts. File changes are made only after explicit user actions. Shell commands are sent to the user's selected local shell session and run with the current account's permissions.
