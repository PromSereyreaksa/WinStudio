# Installation Guide

This guide covers the required tools for running and developing WinStudio on Windows.

## Requirements

- Windows 10 or Windows 11
- Git
- .NET 8 SDK
- FFmpeg installed and available on `PATH`
- Windows App SDK runtime

Visual Studio 2022 is optional, but useful if you want to debug the WinUI app locally.

## 1. Install Git

Install Git from https://git-scm.com/download/win and confirm it works:

```powershell
git --version
```

## 2. Install .NET 8 SDK

Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 and verify:

```powershell
dotnet --version
```

## 3. Install FFmpeg

Install FFmpeg and make sure `ffmpeg.exe` is on your system `PATH`.

Verify:

```powershell
ffmpeg -version
```

If this command fails, WinStudio will not be able to record or process video.

## 4. Install Windows App SDK Runtime

Install the Windows App SDK runtime if it is not already present on your machine. This is
required for the WinUI 3 app shell to launch correctly.

## 5. Clone the Repository

```powershell
git clone <repo-url>
cd WinStudio
```

## 6. Restore, Build, and Run

```powershell
dotnet restore WinStudio.sln
dotnet build WinStudio.sln -c Debug
dotnet run --project src/WinStudio.App/WinStudio.App.csproj -c Debug
```

## 7. Run Tests

```powershell
dotnet test WinStudio.sln -c Debug
```

## Troubleshooting

- `ffmpeg` not found: install FFmpeg and restart your terminal so `PATH` updates.
- App launches and immediately closes: check that the Windows App SDK runtime is installed.
- Build fails after installing new tools: open a new terminal and run the commands again.
