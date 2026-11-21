# Studio.exe

Studio.exe is a Windows desktop development environment built with C# and WPF on .NET 10. It includes a project workspace, Monaco editor, external language-server and debugger integrations, an interactive Windows ConPTY terminal, and a mesh-based 3D scene editor.

## Implemented features

### Editor and languages

- Monaco 0.52.2 is bundled into the build output under `Assets/monaco/vs`. The editor runs locally through WebView2 virtual-host mapping; it does not call a CDN when the app starts.
- JavaScript and TypeScript use Monaco's built-in TypeScript language service.
- Python uses Pyright over the Language Server Protocol (LSP) after the optional language-tools installer runs.
- C# uses Microsoft's Roslyn language server over LSP, including project discovery and Roslyn diagnostics, completion, hover, signatures, definitions, symbols and code actions exposed by the server.
- A local completion/hover fallback remains available when an external language server is not installed.
- Open/create/save files, project explorer, keyboard shortcuts, build and run commands, and compiler/interpreter diagnostics displayed as Monaco markers.

### Debugging

- The Monaco debug panel has gutter breakpoints, Continue, Pause, Step Over, Step In, Step Out, Stop, call stack, variables and a debug console.
- Python uses the `debugpy` DAP adapter.
- JavaScript/Node uses Microsoft's `js-debug` DAP adapter.
- .NET projects use Samsung's `netcoredbg` DAP adapter; Studio builds the selected project in Debug mode and launches its output DLL.
- The optional adapter installer places adapters in the current Windows user's LocalAppData folder. No administrator rights are required.

### Terminal and build

- Native interactive Windows Terminal ConPTY session for PowerShell and CMD.
- Build and run workflows for JavaScript, Python and .NET projects.
- Output and errors are streamed to the app terminal. A Check action runs `node --check`, `python -m py_compile`, or `dotnet build`, then maps recognized diagnostics into Monaco markers.
- GitHub Actions compiles, publishes a self-contained Windows x64 app, and uploads the publish folder as a workflow artifact.
- A separate release workflow creates a GitHub Release from a pushed `v*` tag, attaching `Studio.exe`, a portable ZIP and SHA-256 checksum.

### 3D workspace

- HelixToolkit viewport with orbit, pan, zoom, grid, coordinate axes and view cube.
- Cube, sphere, cylinder, cone, plane and torus primitives.
- XYZ position, scale and rotation controls; diffuse/specular material properties, opacity and per-object colors.
- Editable vertex coordinate grid, triangle subdivision, triangle face extrusion, Laplacian smoothing and face-normal flipping.
- Import Wavefront OBJ and ASCII/binary STL. Export OBJ, ASCII STL and binary glTF (GLB).
- Keyframe animation for object transforms, playback timeline and versioned JSON scene save/load that preserves geometry, material settings and keyframes.

## Requirements

- Windows 10 or Windows 11.
- Microsoft Edge WebView2 Runtime (normally present on current Windows installations).
- .NET 10 SDK for C# build/run and local builds.
- Node.js 22+ with npm for local build-time Monaco bundling and JavaScript debugging.
- Python 3 for Python run/debug workflows. The optional installer adds Pyright and debugpy.
- A network connection is only needed for the first-time optional language-server/debug-adapter installation and for fetching Monaco during local source builds. The published editor itself runs offline.

## Build from source

Open PowerShell in the repository directory:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\setup.ps1
.\build.ps1
```

The build installs pinned Monaco 0.52.2 into the workspace and copies its loader/workers into `src/Studio.App/Assets/monaco/vs`.

Publish a self-contained Windows x64 app:

```powershell
.\build.ps1 -Publish -SelfContained
```

The executable is written to `artifacts\Studio\Studio.exe`. Keep the published `Assets` folder alongside the EXE; the portable ZIP includes it.

## Install language servers and debugger adapters

Open Studio → Extensions → **Install / Update Language Servers + Debug Adapters**. The installer adds:
- Pyright via npm.
- Microsoft Roslyn language server as a .NET global tool.
- Python debugpy via pip.
- Microsoft's JavaScript DAP adapter.
- Samsung netcoredbg for .NET DAP debugging.

These are optional external tools, downloaded to the user profile. Roslyn language server uses MSBuild/project loading; only open source projects you trust.

## GitHub Releases

The release workflow runs when a version tag beginning with `v` is pushed. It restores, builds and publishes the self-contained Windows x64 app, creates a portable ZIP and SHA-256 checksum, and attaches all three to a GitHub Release.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The release contains `Studio.exe`, `Studio-win-x64.zip` and `SHA256SUMS.txt`.

## Remaining limitations

Studio now has real LSP/DAP protocol bridges and a useful primitive/custom-mesh editor, but it is not a full Visual Studio or Blender clone yet:

- The DAP UI supports common breakpoint/stepping/stack/variable flows; advanced features such as conditional/log breakpoints, expression watch editing, memory/disassembly and multi-process attach are not implemented. Adapter installation and runtime debug sessions require external runtimes and have not been exercised by the GitHub compile/publish job.
- C# debugging depends on compatible netcoredbg support for the selected target framework/runtime.
- The 3D editor does not yet include viewport vertex/edge/face selection, bevel/inset/loop-cut tools, sculpt brushes, UV unwrapping, shader-node graphs, rigging, advanced animation curves or GLB import. Its face extrusion currently works on a triangle index selected numerically.
- There is no plugin marketplace, Git GUI, global workspace search, project-template wizard or multi-document tab restoration yet.
- The ConPTY terminal is hosted in a native HWND surface and therefore has WPF airspace limitations.

## Repository structure

```text
Studio.sln
src/Studio.App/
  App.xaml
  MainWindow.xaml
  MainWindow.xaml.cs
  Assets/
    monaco.html
    monaco/vs/                 # generated by scripts/prepare-monaco.mjs
    install-language-tools.ps1
  Models/
  Services/
    StdioProtocolProcess.cs
scripts/
  prepare-monaco.mjs
  install-language-tools.ps1
.github/workflows/
  build.yml
  release.yml
setup.ps1
build.ps1
run.ps1
start.bat
```

## Safety

The app does not require administrator rights. Setup/build scripts do not run source files automatically. Debuggers and language servers execute under the current Windows user and should only be used with projects you trust.
