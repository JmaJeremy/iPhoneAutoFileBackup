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

## macOS

A Python version for macOS lives in [macos/](macos/). It has no build step; see [macos/README.md](macos/README.md) for setup and usage.
