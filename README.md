<div align="center">

  <img src=".github/assets/icons/WinStudio_1.jpg" alt="WinStudio" height="80" />
  <br/><br/>

  <h1>WinStudio</h1>

  <p><strong>Record your screen. Get a polished demo. No editing required.</strong></p>

  <p><em>Native Windows screen recorder focused on fast capture, auto-zoom,<br/>and clean processed output.</em></p>

  <br/>

  [![.NET](https://img.shields.io/badge/.NET_8-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
  [![WinUI](https://img.shields.io/badge/WinUI_3-0078D4?style=flat-square&logo=windows&logoColor=white)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
  [![Windows App SDK](https://img.shields.io/badge/Windows_App_SDK_1.8-0078D4?style=flat-square&logo=windows11&logoColor=white)](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
  [![FFmpeg](https://img.shields.io/badge/FFmpeg-007808?style=flat-square&logo=ffmpeg&logoColor=white)](https://ffmpeg.org/)
  [![Platform](https://img.shields.io/badge/Windows_10_11-0078D4?style=flat-square&logo=windows&logoColor=white)](https://www.microsoft.com/windows)

  <a href="#overview">Overview</a> |
  <a href="#getting-started">Getting Started</a> |
  <a href="#features">Features</a> |
  <a href="#roadmap">Roadmap</a> |
  <a href="#tech-stack">Tech Stack</a> |
  <a href="#contributing">Contributing</a>

  <img src=".github/assets/demo/1.gif" alt="WinStudio demo" width="860" />

  

  



</div>

## Overview

WinStudio is a native Windows desktop app for recording a window or monitor and turning it
into a processed demo video with automatic zoom behavior.

The current MVP is centered on the recording flow: choose a target, record, stop, process,
and review the output files.

---

## Getting Started

### Install

- Windows 10 or Windows 11
- .NET 8 SDK
- FFmpeg installed and available on `PATH`
- Windows App SDK runtime
- Visual Studio 2022 is optional for development

Need setup help or install steps? See [INSTALLATION_GUIDE.md](./INSTALLATION_GUIDE.md).

### Run locally

Clone the repo first:

```powershell
git clone <repo-url>
cd WinStudio
```

From the repository root:

```powershell
dotnet restore WinStudio.sln
dotnet build WinStudio.sln -c Debug
dotnet run --project src/WinStudio.App/WinStudio.App.csproj -c Debug
```

Outputs are written to `C:\Users\<you>\Videos\WinStudio`.

### Test

```powershell
dotnet test WinStudio.sln -c Debug
dotnet test tests/WinStudio.Processing.Tests/WinStudio.Processing.Tests.csproj -c Debug
dotnet test tests/WinStudio.Export.Tests/WinStudio.Export.Tests.csproj -c Debug
```

---

## Features

- [![Capture](https://img.shields.io/badge/Capture-Window%20or%20Monitor-0078D4?style=flat-square)](./src/WinStudio.App) Native WinUI 3 recording flow with target selection before capture.
- [![Toolbar](https://img.shields.io/badge/Toolbar-Floating%20Controls-1F1F1F?style=flat-square)](./src/WinStudio.App) Compact recording toolbar for start, stop, and pause actions during capture.
- [![Auto Zoom](https://img.shields.io/badge/Auto_Zoom-Cursor%20Driven-007808?style=flat-square)](./src/WinStudio.Processing) Processed video zoom reacts to cursor, clicks, drag selection, scroll, and keyboard activity.
- [![Tuning](https://img.shields.io/badge/Tuning-Intensity%20Sensitivity%20Follow-8B5CF6?style=flat-square)](./src/WinStudio.App) Pre-record controls for zoom intensity, zoom sensitivity, and follow speed.
- [![Audio](https://img.shields.io/badge/Audio-System-333333?style=flat-square)](./src/WinStudio.App) Optional system audio capture in the current MVP flow.
- [![Outputs](https://img.shields.io/badge/Outputs-Raw%20%2B%20Processed-0F766E?style=flat-square)](./src/WinStudio.App) Every session writes a raw MP4, processed MP4, cursor JSON, zoom JSON, and FFmpeg logs.
- [![Results](https://img.shields.io/badge/Results-Review%20Screen-DC2626?style=flat-square)](./src/WinStudio.App) Results page for opening outputs and starting a new recording quickly.

---

## Roadmap

- Stabilize cursor-centered zoom so follow behavior is smoother during clicks, drag selection, and typing.
- Wire the existing editor layer into the app for trim and timeline interactions.
- Expand export options beyond the current processed MP4 flow.
- Add microphone capture after the recording pipeline is stable.

---

## Tech Stack

**App**

[![.NET](https://img.shields.io/badge/.NET_8-C%23%2012-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WinUI](https://img.shields.io/badge/WinUI_3-Desktop-0078D4?style=flat-square&logo=windows&logoColor=white)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
[![Windows App SDK](https://img.shields.io/badge/Windows_App_SDK-1.8-0078D4?style=flat-square&logo=windows11&logoColor=white)](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)

**Capture and Processing**

[![FFmpeg](https://img.shields.io/badge/FFmpeg-CLI-007808?style=flat-square&logo=ffmpeg&logoColor=white)](https://ffmpeg.org/)
[![Hooks](https://img.shields.io/badge/Input-Low_Level_Hooks-333333?style=flat-square)](./src/WinStudio.App)
[![Processing](https://img.shields.io/badge/Processing-ZoomRegionGenerator-0F766E?style=flat-square)](./src/WinStudio.Processing)

**Testing**

[![xUnit](https://img.shields.io/badge/xUnit-Tests-512BD4?style=flat-square)](./tests)
[![Coverlet](https://img.shields.io/badge/Coverlet-Collector-6B7280?style=flat-square)](./tests)

---

## Contributing

Contributions should stay focused and verifiable.

- Create a focused branch or PR for one change set
- Run `dotnet build WinStudio.sln -c Debug` before opening a PR
- Run targeted tests while working, then `dotnet test WinStudio.sln -c Debug`
- Include purpose, key changes, test evidence, and screenshots or short recordings for UI work

If you change recording or processing behavior, include the exact test command you ran and
note any output artifacts you inspected.

---

<div align="center">
  <br/>
  <p><em>"Thank you"</em></p>
</div>
