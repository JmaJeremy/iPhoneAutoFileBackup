# iPhone/Android Media Backup Tool for macOS

Python equivalent of the Windows C# iPhoneVideoBackup program. This tool automatically backs up media files from iOS or Android devices over USB on macOS, verifies successful transfers, and optionally deletes files from the device.

## Features

- ✅ **Automatic file discovery** - Finds all media files in DCIM folder
- 📁 **Date-organized backups** - Creates folders named by date (YYYY-MM-DD)
- 🔍 **File verification** - Verifies copied files match originals by size
- 🗑️ **Optional deletion** - Safely delete files after successful backup
- 📊 **Progress tracking** - Shows transfer speed and progress percentage
- 💾 **Disk space checking** - Warns if insufficient space before copying
- 📱 **Multi-device support** - Works with iPhone and Android/Pixel devices

## Supported File Types

- Video: `.mov`, `.mp4`, `.avi`
- Images: `.jpg`, `.jpeg`, `.png`, `.heic`

## Prerequisites

### For iPhone Support

Install libimobiledevice and ifuse via Homebrew:

```bash
brew install libimobiledevice ifuse
```

**Important:** On first connection, you must:
1. Connect your iPhone via USB
2. Unlock your iPhone
3. Tap "Trust This Computer" when prompted
4. Enter your iPhone passcode

### For Android/Pixel Support

Install Android Platform Tools (includes adb):

```bash
brew install android-platform-tools
```

Or download directly from: https://developer.android.com/studio/releases/platform-tools

**Important:** Enable USB debugging on your Android device:
1. Go to Settings → About Phone
2. Tap "Build Number" 7 times to enable Developer Options
3. Go to Settings → Developer Options
4. Enable "USB Debugging"
5. Connect via USB and approve the debugging prompt

## Installation

1. Clone or download this repository
2. Make the script executable:

```bash
chmod +x iphone_backup.py
```

3. Install the appropriate dependencies (see Prerequisites above)

## Usage

### Basic Usage (Interactive Mode)

```bash
python3 iphone_backup.py
```

The script will prompt you for:
- Destination directory
- Device type (iPhone or Android)
- Confirmation to start copying
- Confirmation to delete files after backup

### Command-Line Arguments

```bash
# Specify destination and device type
python3 iphone_backup.py --dest ~/Backups --device iphone

# For Android/Pixel devices
python3 iphone_backup.py --dest ~/Backups --device android
```

### Arguments

- `--dest` - Destination directory for backups (absolute path recommended)
- `--device` - Device type: `iphone`, `android`, or `pixel`

## How It Works

1. **Connection** - Connects to your device via USB
   - iPhone: Mounts filesystem using `ifuse`
   - Android: Communicates via `adb`

2. **Discovery** - Scans the DCIM folder for supported media files

3. **Space Check** - Verifies sufficient disk space on destination

4. **Transfer** - Copies files with progress tracking
   - Skips files already copied (same name and size)
   - Shows transfer speed in MB/s

5. **Verification** - Confirms all files copied successfully by comparing file sizes

6. **Cleanup** (Optional) - Deletes verified files from device if you confirm

## Output Structure

Files are organized by date in the destination directory:

```
~/Backups/
  └── 2025-12-31/
      ├── IMG_0001.JPG
      ├── IMG_0002.MOV
      └── VID_0003.MP4
```

## Troubleshooting

### iPhone Issues

**"libimobiledevice tools not found"**
- Install via: `brew install libimobiledevice ifuse`

**"iPhone not found"**
- Ensure iPhone is unlocked
- Check USB cable connection
- Trust the computer on your iPhone
- Try: `ideviceinfo -s` to test connection

**"Failed to mount iPhone"**
- Unmount any existing mounts: `umount /tmp/iphone_mount_*`
- Restart the lockdownd service: `sudo killall -9 lockdownd`
- Disconnect and reconnect iPhone

### Android Issues

**"adb not found"**
- Install via: `brew install android-platform-tools`
- Or add platform-tools to your PATH

**"Android not found"**
- Enable USB debugging in Developer Options
- Check USB cable connection
- Approve USB debugging prompt on device
- Try: `adb devices` to test connection

**"Could not access DCIM folder"**
- Ensure device is unlocked
- Check that USB debugging is enabled
- Try switching USB mode to "File Transfer" (MTP)

## Comparison with Windows Version

This macOS version provides equivalent functionality to the Windows C# program:

| Feature | Windows (C#) | macOS (Python) |
|---------|--------------|----------------|
| iPhone Support | ✅ MTP via MediaDevices | ✅ AFC via libimobiledevice |
| Android Support | ✅ MTP via MediaDevices | ✅ ADB |
| File Verification | ✅ Size-based | ✅ Size-based |
| Progress Tracking | ✅ | ✅ |
| Transfer Speed | ✅ | ✅ |
| Disk Space Check | ✅ | ✅ |
| Optional Deletion | ✅ | ✅ |
| Date Organization | ✅ | ✅ |

## Security Notes

- The script only reads from and optionally deletes files from your device's DCIM folder
- No data is sent to external servers
- Deletion only occurs after explicit user confirmation
- Files are verified before deletion is offered

## License

Same license as the original Windows version (see parent directory LICENSE file)

## Contributing

Issues and pull requests are welcome. Please maintain compatibility with macOS 10.14+.

## Author

macOS port created as equivalent to the Windows C# iPhoneVideoBackup program.
