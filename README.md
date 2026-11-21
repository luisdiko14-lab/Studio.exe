# Studio.exe

Studio.exe is a Windows desktop development workspace built with C# and WPF on .NET 10. It combines a first-launch wizard, project/file explorer, Monaco editor, an interactive Windows ConPTY terminal, JavaScript/Python/C# workflows, and a lightweight 3D scene editor.

## Current features

- First-launch wizard for studio name, theme, accent, and default project folder. Settings persist in the user's AppData folder.
- Project explorer with open/create/save actions and keyboard shortcuts.
- Monaco editor with JavaScript/TypeScript's built-in TypeScript language service, plus language-aware Python and C# completions, hover documentation, signature help, symbol outline and in-document definition navigation.
- Python and Node.js interactive command-line debugging: Python uses `pdb`; JavaScript uses Node's `node inspect` CLI debugger. Debug sessions run in the ConPTY terminal.
- Build and run commands for JavaScript, Python, and .NET projects. Process output and exit codes are streamed into the workspace terminal.
- Real PowerShell or cmd.exe sessions through the Windows Terminal ConPTY control. Commands run as the current Windows user.
- Interactive HelixToolkit 3D viewport with orbit/pan/zoom, grid, axes, view cube, cube/sphere/cylinder/cone/plane/torus primitives, object transforms, duplicate/delete, JSON scene persistence, and OBJ/ASCII STL export.
- PowerShell setup/build/run scripts.
- GitHub Actions CI on pushes and pull requests, plus a tagged-release workflow that creates a self-contained Windows x64 publish and attaches the executable and portable ZIP to a GitHub Release.

## Requirements

- Windows 10 or Windows 11.
- .NET 10 SDK for local building.
- Microsoft Edge WebView2 Runtime for Monaco (normally present on current Windows installations).
- Node.js for JavaScript execution and Python for Python execution.
- The .NET 10 SDK for C# project build/run.

## Build from source

Open PowerShell in the repository directory:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\setup.ps1
.\build.ps1
```

Publish a self-contained x64 build locally:

```powershell
.\build.ps1 -Publish -SelfContained
```

The executable is written to `artifacts\Studio\Studio.exe`.

## GitHub Releases

The release workflow runs when a version tag beginning with `v` is pushed. It restores, builds, publishes a self-contained single-file Windows x64 application, creates a portable ZIP and SHA-256 checksum, then attaches all release files to the GitHub Release.

Create a release from PowerShell:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The release contains:
- `Studio.exe` — the self-contained Windows x64 executable.
- `Studio-win-x64.zip` — the portable publish folder (includes required sidecar assets).
- `SHA256SUMS.txt` — checksum for the executable.

A normal push to `main` or a pull request runs CI. A successful non-PR CI run also uploads the publish folder as a short-lived workflow artifact.

## Important limitations

This repository contains a functional early-stage IDE foundation, not a complete Visual Studio/Blender replacement:

- Monaco's JavaScript/TypeScript support uses its built-in TypeScript service. Python and C# have richer local/context-aware completion, hover, signatures, symbols and local definition navigation, but do **not** yet run full Pyright/Roslyn language servers. Monaco is currently loaded from jsDelivr, so its editor engine requires internet access.
- Python and Node debugging use their interactive command-line debuggers (`pdb` and `node inspect`). A full Debug Adapter Protocol (DAP) UI with graphical breakpoints, call stack, locals/watch and stepping is not implemented. C# source-level debugging is not wired into the IDE.
- The terminal is hosted by EasyWindowsTerminalControl around Microsoft's ConPTY backend. Its native HWND surface has WPF airspace limitations.
- Language integrations are built-in and do not include an extension marketplace or automatic runtime downloads.
- The 3D editor is a primitive-based scene editor. It supports transform editing, scene save/load and OBJ/ASCII STL export, but not Blender-grade sculpting, arbitrary topology editing, node materials, rigging, animation, or GLB/glTF export.
- Git operations remain available through the shell; there is no dedicated Git UI yet.

## Repository structure

```text
Studio.sln
src/Studio.App/
  App.xaml
  MainWindow.xaml
  MainWindow.xaml.cs
  Assets/monaco.html
  Models/
  Services/
.github/workflows/
  build.yml
  release.yml
setup.ps1
build.ps1
run.ps1
start.bat
```

## Safety

The app does not require administrator rights. Setup scripts check the local SDK and do not silently install runtimes. Files are changed only after explicit user actions. Shell and debugger commands run under the current Windows account.
