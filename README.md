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
- `-y` / `--yes` — start copying without asking, as long as there is enough free space.
- `--wait <seconds>` — count down before starting; press any key during the countdown to cancel. Combined with `-y`, the backup starts automatically when the countdown ends.
- `--autodelete` — delete the originals from the phone without asking, but only if every file copied and verified successfully. If anything failed, you are asked as usual.
- `--retry-delay <seconds>` — how long to wait before reconnecting if the phone is disconnected or the transfer is interrupted (default 10). The program reconnects and resumes, skipping files already copied, and gives up after 3 attempts in a row that make no progress.
- `--help` — show usage.

If `--dest` or `--device` is left out, the program asks for it. Options can be written with `-`, `--` or `/` (e.g. `-wait 10`, `--wait 10`, `/wait 10`). For example, to count down 10 seconds and then back up and clear the phone unattended:

```
iPhoneVideoBackup.exe -dest D:\Backup\iphone -device iphone -y -wait 10 -autodelete
```

While copying, each file shows the elapsed time, the estimated time remaining and the estimated finish time (local time), based on the overall average transfer speed. When copying is done, the program prints the start time, end time and total time before asking whether to delete the originals.

Windows is kept from going to sleep while the backup runs (the display can still turn off). Sleep is allowed again once the program reaches its final "Press Enter to exit" prompt.

### Status beeps

| Beeps | Meaning |
| --- | --- |
| 2 | Copying finished and every file verified |
| 3 | Deleting the originals from the phone finished |
| 10 | Retries ran out (reconnects or re-copies) with files still not copied |

Beeps play on the default sound card and, if the [InpOut](https://www.highrez.co.uk/downloads/inpout32/) driver is available, on the motherboard PC speaker at the same time. To enable the PC speaker, put the InpOut DLL next to `iPhoneVideoBackup.csproj` before building; it is copied to the build output and installed with the program. The program is built as 32-bit, so it uses `inpout32.dll` (which drives the 64-bit InpOut driver on 64-bit Windows); `inpoutx64.dll` is used instead if the program is built as 64-bit. The driver is installed the first time the DLL is loaded, which needs admin rights; the scheduled task runs elevated, so this happens automatically. Without the DLL, the program just beeps on the sound card.

## Publishing

To produce a release build for copying to another machine:

```
dotnet publish -p:PublishProfile=FolderProfile
```

The output is written to `bin\Release\net48\publish\`.

## Installing (auto-launch when an iPhone is connected)

[install.ps1](install.ps1) copies the program to `C:\Program Files\iPhoneVideoBackup` and creates a scheduled task that runs it whenever an iPhone is plugged in. Run it from an elevated (Run as administrator) PowerShell prompt in the repository root:

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

The script:

1. Checks whether the scheduled task (`backup iphone`) already exists, and asks before replacing it.
2. Builds a fresh release (or uses the existing release build if the `dotnet` CLI is not installed).
3. Asks for the folder to back up to (or pass it with `-Dest D:\Backup\iphone`).
4. Asks whether to delete the originals from the phone automatically after a fully verified copy (or pass `-AutoDelete`).
5. Copies the program to Program Files and registers the task for the current user.

The task runs the program with `/dest <your folder> /device iphone /y /wait 10` (plus `/autodelete` if you chose it), so after a 10-second countdown the backup starts without asking for confirmation. It still asks if there isn't enough free space. Use `-Wait <seconds>` to change the countdown.

The task is triggered by the Windows MTP driver's "device connected" event, so it also fires when other MTP devices (such as an Android phone) are connected; the program exits if no iPhone is found.

To uninstall, run from an elevated prompt:

```
Unregister-ScheduledTask -TaskName "backup iphone" -Confirm:$false
Remove-Item "$env:ProgramFiles\iPhoneVideoBackup" -Recurse
```

## macOS

A Python version for macOS lives in [macos/](macos/). It has no build step; see [macos/README.md](macos/README.md) for setup and usage.
