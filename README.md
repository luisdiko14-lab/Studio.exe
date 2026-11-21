# Studio.exe

Studio.exe is an early Windows desktop development-studio project built with C# and WPF on .NET 10. It combines a first-launch studio wizard, a project/file workspace, a persistent PowerShell or Command Prompt session, basic JavaScript/Python/C# run integrations, and a lightweight WPF 3D scene editor.

> **Project status:** this repository now contains a buildable first implementation, not the complete commercial-grade IDE described in the original product brief. Read the limitations below before relying on advanced editor, terminal, debugging, or modeling capabilities.

## Current features

- First-launch wizard for studio name, theme, accent, and default project folder. Settings persist in the user's AppData folder.
- WPF workspace with project-folder browsing, open/create/save file actions, a Monaco editor tab, a native ConPTY terminal tab, a HelixToolkit 3D viewport, and exactly three language integrations.
- Real PowerShell or cmd.exe sessions. The legacy redirected shell remains available, and the new native terminal tab uses the Windows Terminal ConPTY control for interactive console applications, VT/ANSI rendering and terminal resizing. Commands run as the current Windows user.
- Run JavaScript files with Node.js, Python files with Python, and C# projects with dotnet run. Build searches the opened project root for a solution or project file.
- Exactly three language integration toggles: JavaScript, Python, and C#.
- Interactive HelixToolkit 3D viewport with orbit/pan/zoom camera controls, grid, coordinate axes, view cube, cube/sphere/cylinder/cone/plane/torus primitives, numeric transforms, duplicate/delete, JSON scene save/load, and OBJ/ASCII STL export.
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

- Monaco is available in its own editor tab and currently loads its editor engine from jsDelivr, so internet access is required. JavaScript/TypeScript uses Monaco's built-in TypeScript language service; Python and C# currently have syntax modes and a small set of snippet completions, not full Pyright/Roslyn language-server IntelliSense. The WPF text editor remains as an offline fallback. The Monaco tab is an early integration and not yet fully synchronized for every editor workflow.
- The native terminal uses the third-party EasyWindowsTerminalControl wrapper around Microsoft's Windows Terminal ConPTY backend. Its WPF host uses a native HWND, so WPF overlays cannot be rendered over the terminal surface. The original redirected shell is still present as a fallback.
- The three language cards enable/disable integrations, but are not a downloadable extension marketplace. Runtime and SDK detection is basic and errors are reported by the launched process.
- Source-level debugging (DAP adapters, breakpoints, variable inspection and stepping), Python/Pyright and C#/Roslyn language-server integration, mesh sculpting/edit mode, material node editor, animation timeline, GLB/glTF export, Git UI, project templates, global search and tab restoration are not implemented yet. Use the integrated shell for Git and package-manager commands.
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
