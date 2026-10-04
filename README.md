# iPhoneAutoFileBackup
Program written in C# to automatically backup files from an iPhone over USB on Windows, verify if the files were copied successfully, and then delete them from the iPhone

## Building (Windows)

Requirements:

- Windows
- [.NET SDK](https://dotnet.microsoft.com/download) (provides the `dotnet` CLI), or Visual Studio
- .NET Framework 4.8 (the project targets `net48`; it is included with current versions of Windows)

Build from the repository root:

```
dotnet build
```

The `MediaDevices` NuGet package is restored automatically. The executable is written to `bin\Debug\net48\iPhoneVideoBackup.exe`.

## Running

Connect the phone over USB, unlock it, and allow the computer to access it. Then either run the built executable or run straight from source:

```
dotnet run --project iPhoneVideoBackup.csproj -- --dest C:\Backup --device iphone
```

- `--dest` — absolute path of the backup folder. Files are copied into a subfolder named after today's date (`yyyy-MM-dd`).
- `--device` — `iphone` or `pixel`.

If either option is left out, the program asks for it.

## Publishing

To produce a release build for copying to another machine:

```
dotnet publish -p:PublishProfile=FolderProfile
```

The output is written to `bin\Release\net48\publish\`.

## Installing (auto-launch when an iPhone is connected)

[install.ps1](install.ps1) copies the program to `C:\Program Files\iPhoneVideoBackup` and creates a scheduled task that launches it whenever an iPhone is plugged in. Run it from an elevated (Run as administrator) PowerShell prompt in the repository root:

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

The script:

1. Checks whether the scheduled task (`backup iphone`) already exists, and asks before replacing it.
2. Builds a fresh release (or uses the existing release build if the `dotnet` CLI is not installed).
3. Asks for the folder to back up to (or pass it with `-Dest D:\Backup\iphone`).
4. Copies the program to Program Files and registers the task for the current user.

The task is triggered by the Windows MTP driver's "device connected" event, so it also fires when other MTP devices (such as an Android phone) are connected; the program exits if no iPhone is found.

To uninstall, run from an elevated prompt:

```
Unregister-ScheduledTask -TaskName "backup iphone" -Confirm:$false
Remove-Item "$env:ProgramFiles\iPhoneVideoBackup" -Recurse
```

## macOS

A Python version for macOS lives in [macos/](macos/). It has no build step; see [macos/README.md](macos/README.md) for setup and usage.
