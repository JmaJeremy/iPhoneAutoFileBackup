# Installation Guide for macOS

## Quick Start

### 1. Install Homebrew (if not already installed)

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

### 2. Install Dependencies

#### For iPhone Support

```bash
brew install libimobiledevice ifuse
```

#### For Android/Pixel Support

```bash
brew install android-platform-tools
```

### 3. Download the Script

```bash
# Clone the repository or download iphone_backup.py
cd ~/Downloads  # or your preferred location
```

### 4. Make Executable

```bash
chmod +x iphone_backup.py
```

### 5. Run the Script

```bash
python3 iphone_backup.py
```

## Detailed Setup Instructions

### iPhone Setup

1. **Install libimobiledevice and ifuse:**
   ```bash
   brew install libimobiledevice ifuse
   ```

2. **Connect your iPhone:**
   - Use a genuine Apple USB cable or certified MFi cable
   - Connect to a USB port (not through a hub if possible)

3. **Trust your Mac:**
   - Unlock your iPhone
   - A prompt will appear: "Trust This Computer?"
   - Tap "Trust"
   - Enter your iPhone passcode

4. **Verify connection:**
   ```bash
   ideviceinfo -s
   ```
   You should see your device's serial number.

5. **Test mounting (optional):**
   ```bash
   mkdir ~/iphone_test
   ifuse ~/iphone_test
   ls ~/iphone_test/DCIM
   umount ~/iphone_test
   rmdir ~/iphone_test
   ```

### Android/Pixel Setup

1. **Install Android Platform Tools:**
   ```bash
   brew install android-platform-tools
   ```

2. **Enable Developer Options on your Android device:**
   - Go to **Settings** → **About Phone**
   - Tap **Build Number** 7 times
   - You'll see "You are now a developer!"

3. **Enable USB Debugging:**
   - Go to **Settings** → **System** → **Developer Options**
   - Enable **USB Debugging**

4. **Connect your Android device:**
   - Use a good quality USB cable
   - Connect to your Mac

5. **Authorize your Mac:**
   - A prompt will appear on your Android device
   - Check "Always allow from this computer"
   - Tap "OK" or "Allow"

6. **Verify connection:**
   ```bash
   adb devices
   ```
   You should see your device listed with "device" status.

## Troubleshooting Installation

### Homebrew Issues

**"brew: command not found"**
```bash
# Install Homebrew first
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

# Add to PATH (for Apple Silicon Macs)
echo 'eval "$(/opt/homebrew/bin/brew shellenv)"' >> ~/.zprofile
eval "$(/opt/homebrew/bin/brew shellenv)"
```

### libimobiledevice Issues

**"Could not connect to lockdownd"**
```bash
# Restart the lockdown daemon
sudo killall -9 lockdownd

# Disconnect and reconnect your iPhone
```

**"Permission denied" when mounting**
```bash
# Install osxfuse (now called macfuse)
brew install macfuse

# You may need to allow the kernel extension in:
# System Preferences → Security & Privacy → General
```

### ADB Issues

**"adb: command not found"**
```bash
# Ensure platform-tools is in PATH
echo 'export PATH="$PATH:~/Library/Android/sdk/platform-tools"' >> ~/.zshrc
source ~/.zshrc

# Or reinstall via Homebrew
brew install android-platform-tools
```

**"device unauthorized"**
- Disconnect and reconnect your Android device
- Revoke USB debugging authorizations on your device:
  - Settings → Developer Options → Revoke USB debugging authorizations
- Reconnect and approve the prompt again

## Python Version

This script requires Python 3.6 or later. macOS 10.14+ includes Python 3 by default.

Check your Python version:
```bash
python3 --version
```

If you need to install or update Python:
```bash
brew install python3
```

## Creating an Alias (Optional)

For easier access, create an alias:

```bash
# Add to ~/.zshrc (or ~/.bash_profile for bash)
echo 'alias iphone-backup="python3 /path/to/iphone_backup.py"' >> ~/.zshrc
source ~/.zshrc

# Now you can run:
iphone-backup --dest ~/Backups --device iphone
```

## System Requirements

- **macOS:** 10.14 (Mojave) or later
- **Python:** 3.6 or later (included in macOS)
- **Disk Space:** Sufficient space for your media files
- **USB Port:** USB 2.0 or later

## First Run Checklist

- [ ] Homebrew installed
- [ ] libimobiledevice/ifuse installed (for iPhone)
- [ ] android-platform-tools installed (for Android)
- [ ] Device connected via USB
- [ ] Device unlocked
- [ ] Computer trusted on device
- [ ] Script has execute permissions
- [ ] Destination directory exists or can be created

## Getting Help

If you encounter issues:

1. Check the troubleshooting section in README.md
2. Verify all dependencies are installed: `brew list`
3. Test device connection independently:
   - iPhone: `ideviceinfo -s`
   - Android: `adb devices`
4. Check system logs: `Console.app` → search for "ifuse" or "adb"

## Uninstallation

To remove the dependencies:

```bash
# Remove iPhone tools
brew uninstall ifuse libimobiledevice

# Remove Android tools
brew uninstall android-platform-tools

# Remove the script
rm /path/to/iphone_backup.py
```
