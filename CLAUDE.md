# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository purpose

Two independent, parallel implementations of the same CLI tool for backing up media files from a USB-connected phone (iPhone or Android/Pixel), verifying the copy, and optionally deleting the originals from the device:

- **Windows / C#** ([Program.cs](Program.cs)) — the primary implementation, single-file console app.
- **macOS / Python** ([macos/iphone_backup.py](macos/iphone_backup.py)) — a from-scratch port with equivalent behavior, since the Windows MTP approach doesn't work on macOS.

When changing behavior (new file extensions, new prompts, changed flow, etc.), consider whether the change should be mirrored in both implementations — they're documented as functionally equivalent (see the comparison table in [macos/README.md](macos/README.md)).

## Build / run / publish

This is a .NET Framework 4.8 console app (not .NET Core), built with the classic MSBuild toolchain via `dotnet` CLI or Visual Studio.

```
dotnet build                     # build
dotnet run --project iPhoneVideoBackup.csproj -- --dest C:\Backup --device iphone
dotnet publish -p:PublishProfile=FolderProfile   # publish to bin\Release\net48\publish\ (see Properties/PublishProfiles/FolderProfile.pubxml)
```

There is no test project/framework in this repo — do not invent one unless asked.

The macOS Python script has no build step and no third-party pip dependencies (see [macos/requirements.txt](macos/requirements.txt)); all device access goes through external Homebrew CLI tools (`ifuse`/`libimobiledevice` for iPhone, `adb` for Android) invoked via `subprocess`.

```
python3 macos/iphone_backup.py --dest ~/Backups --device iphone
```

## Architecture (Windows / Program.cs)

Everything lives in one file, `Program.cs`, structured as a linear pipeline in `Main` plus standalone static helper methods:

1. **Argument/prompt resolution** — `--dest`/`/dest` and `--device`/`/device` are parsed manually from `args`; anything missing is prompted for interactively via `Console.ReadLine`. `deviceType` is normalized to `"iphone"` or `"pixel"`.
2. **Destination validation** — must be an absolute, rooted path on an existing drive; a `yyyy-MM-dd` subfolder is appended and created.
3. **Device discovery** — uses the `MediaDevices` NuGet package (MTP protocol) to enumerate devices and match by `FriendlyName` substring (`"iPhone"` or `"Pixel"`).
4. **DCIM traversal** — the DCIM subpath differs by device type (`\Internal Storage\DCIM` for iPhone vs `\Internal shared storage\DCIM` for Pixel); files matching `SupportedExtensions` (`*.MOV, *.MP4, *.AVI, *.JPG`) are collected with size, then sorted by filename.
5. **Space check** (`CheckDriveSpace`) — compares total source size against `DriveInfo.AvailableFreeSpace`; on shortfall, prompts to continue anyway.
6. **Copy** (`CopyFiles`) — streams each file via `device.DownloadFile`, skips files that already exist at the destination with a matching size, and prints per-file progress/percent/speed (MB/s).
7. **Verify** (`VerifyFiles`) — post-copy, re-checks destination file existence + size match against the source list (size-based only, not checksum-based, despite the unused `ComputeFileChecksum`/`ComputeDeviceFileChecksum` SHA256 helpers still present in the file).
8. **Delete** (`HandleDeletion`) — only offered for files that passed verification, and only after an explicit `Y` confirmation; deletes from the device via `device.DeleteFile`.

Control flow is deliberately fail-soft: copy/verify/delete are wrapped so that an interrupted transfer still runs verification and reports partial results rather than aborting silently.

## Architecture (macOS / macos/iphone_backup.py)

Mirrors the same pipeline via a small class hierarchy instead of a device-abstraction library:

- `DeviceBackup` — base class holding shared logic: disk space check, SHA256 checksum helper (also unused for verification, which is size-based like the Windows version), and `verify_files`.
- `iPhoneBackup(DeviceBackup)` — connects via `ideviceinfo`/`ifuse` (mounts the device to a temp mountpoint under `/tmp`), walks the mounted `DCIM` folder with `os.walk`, copies with `shutil.copy2`, deletes with `os.remove`, unmounts on cleanup.
- `AndroidBackup(DeviceBackup)` — drives everything through `adb` subprocess calls (`adb devices`, `adb shell find`, `adb shell stat`, `adb pull`, `adb shell rm`) against `/sdcard/DCIM`.
- `main()` reproduces the same CLI flag / interactive-prompt resolution, date-folder creation, space-check-then-confirm, copy-then-verify-then-optional-delete flow as the C# version, plus a `finally` block that unmounts the iPhone if one was mounted.

Supported extensions are broader here than on Windows: `.mov, .mp4, .avi, .jpg, .jpeg, .png, .heic` (case-insensitive) — keep this list and the Windows `SupportedExtensions` array in sync deliberately, or note the divergence, when adding formats.
