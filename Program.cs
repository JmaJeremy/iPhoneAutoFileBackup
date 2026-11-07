using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MediaDevices;

namespace iPhoneVideoBackup
{
    class Program
    {
        // Define a constant array of file extensions to handle
        private static readonly string[] SupportedExtensions = { "*.MOV", "*.MP4", "*.AVI", "*.JPG" };

        // Number of attempts (and delay between them) when a device read fails
        private const int MaxDownloadAttempts = 3;
        private const int DownloadRetryDelayMs = 2000;

        // Total attempts (initial + retries) to copy and verify the full file set
        private const int MaxVerificationRounds = 3;

        // Attempts to reconnect (and default wait before each) when the connection to the device is lost
        private const int MaxReconnectAttempts = 3;
        private const int DefaultReconnectDelaySeconds = 10;
        private const int MaxReconnectDelaySeconds = 3600;

        // Upper limit for the --wait countdown
        private const int MaxWaitSeconds = 3600;

        // Held for the life of the process so a second copy can tell one is already running
        private const string InstanceMutexName = @"Global\iPhoneVideoBackup";
        private static System.Threading.Mutex instanceMutex;

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Show usage and exit straight away, without waiting for Enter
            if (args.Any(a => IsOption(a, "help") || IsOption(a, "h") || a == "/?"))
            {
                PrintUsage();
                return;
            }

            // Quit if another instance is already running
            if (!TryClaimSingleInstance())
            {
                Console.WriteLine("⏹️ iPhoneVideoBackup is already running. Exiting.");
                System.Threading.Thread.Sleep(3000); // Leave the message up long enough to read
                return;
            }

            // Keep Windows from going to sleep while the backup runs (the display may still turn off)
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

            try
            {
                // Parse command-line arguments
                string destinationRoot = null;
                string deviceType = null;
                bool autoConfirm = false;
                bool autoDelete = false;
                int waitSeconds = 0;
                int reconnectDelaySeconds = DefaultReconnectDelaySeconds;
                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (IsOption(arg, "dest") || IsOption(arg, "device") || IsOption(arg, "retry-delay") || IsOption(arg, "wait"))
                    {
                        if (i + 1 >= args.Length)
                        {
                            Console.WriteLine($"❌ {arg} requires a value.");
                            PrintUsage();
                            return;
                        }
                        var value = args[++i];
                        if (IsOption(arg, "dest"))
                        {
                            destinationRoot = value;
                        }
                        else if (IsOption(arg, "device"))
                        {
                            deviceType = value.Trim().ToLowerInvariant();
                        }
                        else if (IsOption(arg, "wait"))
                        {
                            if (!int.TryParse(value, out waitSeconds) || waitSeconds < 0 || waitSeconds > MaxWaitSeconds)
                            {
                                Console.WriteLine($"❌ {arg} must be a number of seconds between 0 and {MaxWaitSeconds}.");
                                return;
                            }
                        }
                        else if (!int.TryParse(value, out reconnectDelaySeconds) || reconnectDelaySeconds < 0 || reconnectDelaySeconds > MaxReconnectDelaySeconds)
                        {
                            Console.WriteLine($"❌ {arg} must be a number of seconds between 0 and {MaxReconnectDelaySeconds}.");
                            return;
                        }
                    }
                    else if (IsOption(arg, "yes") || IsOption(arg, "y"))
                    {
                        autoConfirm = true;
                    }
                    else if (IsOption(arg, "autodelete"))
                    {
                        autoDelete = true;
                    }
                    else
                    {
                        Console.WriteLine($"❌ Unknown option '{arg}'.");
                        PrintUsage();
                        return;
                    }
                }

                // If not provided, prompt the user for the destination directory
                if (string.IsNullOrWhiteSpace(destinationRoot))
                {
                    Console.Write("📁 Enter destination directory (required): ");
                    var inputDest = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(inputDest))
                    {
                        destinationRoot = inputDest.Trim();
                    }
                    else
                    {
                        Console.WriteLine("❌ Destination directory is required. Exiting.");
                        return;
                    }
                }

                // Validate the supplied path and drive
                try
                {
                    // Check if path is absolute
                    if (!Path.IsPathRooted(destinationRoot))
                    {
                        Console.WriteLine("❌ Please provide an absolute path (e.g., C:\\Backup). Exiting.");
                        return;
                    }
                    // Check if drive exists
                    var root = Path.GetPathRoot(destinationRoot);
                    if (string.IsNullOrWhiteSpace(root) || !Directory.GetLogicalDrives().Any(d => string.Equals(d.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine($"❌ Drive '{root}' does not exist. Exiting.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Invalid path: {ex.Message} Exiting.");
                    return;
                }

                // Prompt for device type if not set
                if (string.IsNullOrWhiteSpace(deviceType))
                {
                    Console.WriteLine("Select device type:");
                    Console.WriteLine("  1. iPhone");
                    Console.WriteLine("  2. Pixel");
                    Console.Write("Enter 1 or 2: ");
                    var deviceChoice = Console.ReadLine();
                    if (deviceChoice == "1") deviceType = "iphone";
                    else if (deviceChoice == "2") deviceType = "pixel";
                    else
                    {
                        Console.WriteLine("❌ Invalid device type selection. Exiting.");
                        return;
                    }
                }

                // Append today's date as a subfolder in yyyy-MM-dd format
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                destinationRoot = Path.Combine(destinationRoot, today);

                // Give the user a chance to cancel before anything happens
                if (waitSeconds > 0 && !Countdown(waitSeconds))
                {
                    Console.WriteLine("⏹️ Cancelled.");
                    return;
                }

                // List all detected devices and their friendly names
                var devices = MediaDevice.GetDevices();
                Console.WriteLine($"Devices found: {devices.Count()}");
                foreach (var mediaDevice in devices)
                {
                    Console.WriteLine($"Device: {mediaDevice.FriendlyName}");
                }

                // Find the device and connect to it BEFORE accessing files
                MediaDevice device = ConnectToDevice(deviceType);
                if (device == null)
                {
                    return;
                }

                // Now it's safe to access files and directories
                // Path to the DCIM folder where photos and videos are stored
                string dcimPath;
                if (deviceType == "pixel")
                {
                    dcimPath = @"\Internal shared storage\DCIM";
                }
                else
                {
                    dcimPath = @"\Internal Storage\DCIM";
                }
                var videoFiles = new List<(string sourcePath, string fileName, long size)>();

                // Prompt the user to commence copying files, unless -y was given
                // (the space check below still asks if there isn't enough room)
                if (!autoConfirm)
                {
                    Console.Write("❓ Do you want to commence copying files? (Y/N): ");
                    var startResponse = Console.ReadLine();
                    if (startResponse == null || !startResponse.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("⏹️ Operation cancelled by user.");
                        return;
                    }
                }

                Console.WriteLine($"\n📂 Starting to copy files from {dcimPath} to {destinationRoot}...\n");

                // Enumerate all directories in the DCIM folder
                foreach (var folder in device.GetDirectories(dcimPath))
                {
                    // Find files matching any of the supported extensions
                    foreach (var extension in SupportedExtensions)
                    {
                        foreach (var file in device.GetFiles(folder, extension))
                        {
                            var fileInfo = device.GetFileInfo(file);
                            // Add file details (path, name, size) to the list
                            videoFiles.Add((file, Path.GetFileName(file), (long)fileInfo.Length)); // Explicitly cast ulong to long
                        }
                    }
                }

                // Exit if no video files are found
                if (videoFiles.Count == 0)
                {
                    Console.WriteLine("⚠️ No supported files found.");
                    return;
                }

                // Sort videoFiles by fileName before copying
                videoFiles = videoFiles.OrderBy(f => f.fileName).ToList();

                // Check if there is sufficient space on the destination drive
                long totalSizeNeeded = videoFiles.Sum(f => f.size);
                if (!CheckDriveSpace(destinationRoot, totalSizeNeeded))
                {
                    Console.Write("\n❓ Insufficient space on destination drive. Do you still want to continue? (Y/N): ");
                    var continueResponse = Console.ReadLine();
                    if (continueResponse == null || !continueResponse.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("⏹️ Operation cancelled by user.");
                        device.Disconnect();
                        return;
                    }
                }

                // Create the destination directory only now that files are about to be copied
                Directory.CreateDirectory(destinationRoot);

                // Variables to track verification results
                List<string> verified = new List<string>();
                List<string> failed = new List<string>();

                // Copy, then verify; if any files failed verification, retry just those files.
                // If the transfer is interrupted (e.g. the phone was disconnected), reconnect and resume.
                var filesToCopy = videoFiles;
                int verificationRound = 1;
                int reconnectAttempts = 0;
                var progress = new CopyProgress();
                while (true)
                {
                    // ETA is based on what this pass still has to copy (files already at the destination are skipped)
                    progress.BytesRemaining = filesToCopy.Where(f => !IsAlreadyCopied(Path.Combine(destinationRoot, f.fileName), f.size)).Sum(f => f.size);

                    bool interrupted = false;
                    try
                    {
                        CopyFiles(device, filesToCopy, destinationRoot, progress);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n⚠️ Transfer was interrupted: {ex.Message}");
                        interrupted = true;
                    }

                    // Verify that the copied files match the originals
                    int previouslyVerified = verified.Count;
                    VerifyFiles(videoFiles, destinationRoot, out verified, out failed);

                    if (failed.Count == 0)
                    {
                        break;
                    }
                    filesToCopy = videoFiles.Where(f => failed.Contains(f.fileName)).ToList();

                    if (interrupted)
                    {
                        // Only interruptions that made no progress count toward giving up
                        if (verified.Count > previouslyVerified)
                        {
                            reconnectAttempts = 0;
                        }
                        device = Reconnect(device, deviceType, reconnectDelaySeconds, ref reconnectAttempts);
                        if (device == null)
                        {
                            Console.WriteLine($"\n❌ Could not resume the transfer after {MaxReconnectAttempts} attempts. Giving up.");
                            break;
                        }
                        continue;
                    }

                    if (verificationRound == MaxVerificationRounds)
                    {
                        break;
                    }
                    verificationRound++;
                    Console.WriteLine($"\n🔁 {failed.Count} file(s) failed verification. Retrying (attempt {verificationRound}/{MaxVerificationRounds})...");
                }

                // Summarize how long the copy took
                var endTime = DateTime.Now;
                Console.WriteLine("\n⏱️ Copy summary:");
                Console.WriteLine($"   Started:  {FormatLocalTime(progress.StartTime)}");
                Console.WriteLine($"   Finished: {FormatLocalTime(endTime)}");
                Console.WriteLine($"   Total time: {FormatDuration(endTime - progress.StartTime)}");
                Console.WriteLine($"   Copied: {progress.BytesCopied / (1024.0 * 1024.0 * 1024.0):F2} GB at an average of {progress.AverageSpeed / (1024.0 * 1024.0):F2} MB/s");

                // 2 beeps: everything copied and verified. 10 beeps: retries ran out with files still missing.
                Beep(failed.Count == 0 ? 2 : 10);

                if (device != null)
                {
                    // Handle deletion of successfully copied files based on user input
                    HandleDeletion(device, verified, autoDelete && failed.Count == 0);

                    // Disconnect the device
                    device.Disconnect();
                }
                else if (verified.Count > 0)
                {
                    Console.WriteLine("⏭️ Skipped deletion because the device is no longer connected.");
                }
                Console.WriteLine("\n🎉 Done.");
            }
            catch (Exception ex)
            {
                // Catch and display any errors that occur during the process
                Console.WriteLine($"❌ An error occurred: {ex.Message}");
            }
            finally
            {
                // Allow sleep again, so a window left open at the prompt below doesn't keep the PC awake
                SetThreadExecutionState(ES_CONTINUOUS);

                // Wait for the user to press Enter before closing the program
                Console.WriteLine("\nPress Enter to exit...");
                Console.ReadLine();
            }
        }

        // Sleep prevention (kernel32 SetThreadExecutionState)
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        // Status beeps
        private const int BeepFrequencyHz = 1000;
        private const int BeepDurationMs = 200;
        private const int BeepGapMs = 150;

        // Plays the given number of beeps on the default sound card and, if the InpOut driver is
        // available, on the built-in PC speaker at the same time
        static void Beep(int count)
        {
            for (int i = 0; i < count; i++)
            {
                bool speakerOn = PcSpeaker.Start(BeepFrequencyHz);
                try
                {
                    // Console.Beep is routed to the default audio device on modern Windows, and blocks for the duration
                    Console.Beep(BeepFrequencyHz, BeepDurationMs);
                }
                catch
                {
                    System.Threading.Thread.Sleep(BeepDurationMs);
                }
                if (speakerOn)
                {
                    PcSpeaker.Stop();
                }
                System.Threading.Thread.Sleep(BeepGapMs);
            }
        }

        // Drives the motherboard PC speaker through the programmable interval timer, using port I/O
        // from the InpOut driver (inpoutx64.dll for a 64-bit process, inpout32.dll for 32-bit). If the
        // DLL or driver isn't available, the speaker is skipped and only the sound card beeps.
        static class PcSpeaker
        {
            private const short TimerControlPort = 0x43;
            private const short TimerChannel2Port = 0x42;
            private const short SpeakerControlPort = 0x61;
            private const int TimerBaseFrequencyHz = 1193180;

            private static bool? available;

            [DllImport("inpoutx64.dll", EntryPoint = "Out32")] private static extern void Out64(short port, short data);
            [DllImport("inpoutx64.dll", EntryPoint = "Inp32")] private static extern short Inp64(short port);
            [DllImport("inpoutx64.dll", EntryPoint = "IsInpOutDriverOpen")] private static extern int IsOpen64();
            [DllImport("inpout32.dll", EntryPoint = "Out32")] private static extern void Out32(short port, short data);
            [DllImport("inpout32.dll", EntryPoint = "Inp32")] private static extern short Inp32(short port);
            [DllImport("inpout32.dll", EntryPoint = "IsInpOutDriverOpen")] private static extern int IsOpen32();

            private static void Out(short port, short data)
            {
                if (Environment.Is64BitProcess) Out64(port, data); else Out32(port, data);
            }

            private static short Inp(short port)
            {
                return Environment.Is64BitProcess ? Inp64(port) : Inp32(port);
            }

            // Checked once; loading the DLL also installs/starts the driver (needs admin the first time)
            private static bool IsAvailable()
            {
                if (available == null)
                {
                    try
                    {
                        available = (Environment.Is64BitProcess ? IsOpen64() : IsOpen32()) != 0;
                    }
                    catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
                    {
                        available = false;
                    }
                    if (available == false)
                    {
                        Console.WriteLine("ℹ️ PC speaker unavailable (InpOut driver not found or not running); beeping on the sound card only.");
                    }
                }
                return available.Value;
            }

            // Starts a tone; returns false if the speaker couldn't be used
            public static bool Start(int frequencyHz)
            {
                if (!IsAvailable())
                {
                    return false;
                }
                try
                {
                    int divisor = TimerBaseFrequencyHz / frequencyHz;
                    Out(TimerControlPort, 0xB6); // Channel 2, low then high byte, square wave
                    Out(TimerChannel2Port, (short)(divisor & 0xFF));
                    Out(TimerChannel2Port, (short)((divisor >> 8) & 0xFF));
                    Out(SpeakerControlPort, (short)(Inp(SpeakerControlPort) | 0x03)); // Connect the timer to the speaker
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public static void Stop()
            {
                try
                {
                    Out(SpeakerControlPort, (short)(Inp(SpeakerControlPort) & 0xFC));
                }
                catch
                {
                    // Nothing more we can do; the speaker will be silenced when the driver is unloaded
                }
            }
        }

        // Create the named mutex; returns false if another instance already owns it
        static bool TryClaimSingleInstance()
        {
            try
            {
                instanceMutex = new System.Threading.Mutex(true, InstanceMutexName, out bool createdNew);
                return createdNew;
            }
            catch (UnauthorizedAccessException)
            {
                // The mutex exists but belongs to an elevated instance (e.g. the scheduled task)
                return false;
            }
        }

        // Matches an option written as -name, --name or /name
        static bool IsOption(string arg, string name)
        {
            return arg.Equals("-" + name, StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--" + name, StringComparison.OrdinalIgnoreCase)
                || arg.Equals("/" + name, StringComparison.OrdinalIgnoreCase);
        }

        // Counts down, one line per second; returns false if a key was pressed to cancel.
        // Without an interactive console the keypress check is skipped and the countdown just runs.
        static bool Countdown(int seconds)
        {
            bool canReadKeys = true;
            try
            {
                var _ = Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                canReadKeys = false;
            }

            if (canReadKeys)
            {
                Console.WriteLine("Press any key to cancel.");
            }
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                Console.WriteLine($"⏳ Starting in {remaining} second{(remaining == 1 ? "" : "s")}...");
                var end = DateTime.Now.AddSeconds(1);
                while (DateTime.Now < end)
                {
                    if (canReadKeys && Console.KeyAvailable)
                    {
                        Console.ReadKey(true);
                        return false;
                    }
                    System.Threading.Thread.Sleep(50);
                }
            }
            Console.WriteLine();
            return true;
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: iPhoneVideoBackup.exe [--dest <folder>] [--device iphone|pixel] [-y] [--wait <seconds>]");
            Console.WriteLine("                             [--autodelete] [--retry-delay <seconds>]");
            Console.WriteLine();
            Console.WriteLine("Backs up photos and videos from a USB-connected phone to <folder>\\yyyy-MM-dd,");
            Console.WriteLine("verifies the copies, and optionally deletes the originals from the phone.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --dest <folder>          Absolute path of the backup folder. Prompted for if omitted.");
            Console.WriteLine("  --device iphone|pixel    Type of phone to back up. Prompted for if omitted.");
            Console.WriteLine("  -y, --yes                Start copying without asking, as long as there is enough free space.");
            Console.WriteLine("  --wait <seconds>         Count down before starting; press any key during the countdown to cancel.");
            Console.WriteLine("                           With -y, the backup then starts automatically.");
            Console.WriteLine("  --autodelete             Delete the originals from the phone without asking, but only if every");
            Console.WriteLine("                           file copied and verified successfully. Otherwise you are asked as usual.");
            Console.WriteLine("  --retry-delay <seconds>  Seconds to wait before reconnecting when the connection to the phone");
            Console.WriteLine($"                           is lost (default {DefaultReconnectDelaySeconds}). Gives up after {MaxReconnectAttempts} failed attempts.");
            Console.WriteLine("  -h, --help               Show this help.");
            Console.WriteLine();
            Console.WriteLine("Options can be written with -, -- or /, e.g. -wait, --wait or /wait.");
        }

        // Find the device by its friendly name and connect to it; returns null if that fails
        static MediaDevice ConnectToDevice(string deviceType)
        {
            string nameMatch = deviceType == "iphone" ? "iPhone" : deviceType == "pixel" ? "Pixel" : null;
            try
            {
                var device = nameMatch == null ? null : MediaDevice.GetDevices().FirstOrDefault(d => d.FriendlyName.IndexOf(nameMatch, StringComparison.OrdinalIgnoreCase) >= 0);
                if (device == null)
                {
                    Console.WriteLine($"❌ {deviceType.First().ToString().ToUpper() + deviceType.Substring(1)} not found.");
                    return null;
                }

                device.Connect();
                if (!device.IsConnected)
                {
                    Console.WriteLine("❌ Device is not connected.");
                    return null;
                }

                Console.WriteLine($"✅ Connected to {deviceType} device.");
                return device;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to connect: {ex.Message}");
                return null;
            }
        }

        // Wait, then reconnect to the device; returns null once MaxReconnectAttempts have been used up
        static MediaDevice Reconnect(MediaDevice device, string deviceType, int delaySeconds, ref int attempts)
        {
            try
            {
                device.Disconnect();
            }
            catch
            {
                // The old connection is most likely already gone
            }

            while (attempts < MaxReconnectAttempts)
            {
                attempts++;
                Console.WriteLine($"\n🔌 Waiting {delaySeconds}s, then reconnecting to the device (attempt {attempts}/{MaxReconnectAttempts})...");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));

                var newDevice = ConnectToDevice(deviceType);
                if (newDevice != null)
                {
                    Console.WriteLine("🔁 Resuming copy...\n");
                    return newDevice;
                }
            }
            return null;
        }

        // Method to compute SHA256 checksum of a file
        static string ComputeFileChecksum(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // Method to compute SHA256 checksum of a file on the device
        static string ComputeDeviceFileChecksum(MediaDevice device, string sourcePath)
        {
            using (var sha256 = SHA256.Create())
            using (var ms = new MemoryStream())
            {
                device.DownloadFile(sourcePath, ms);
                ms.Position = 0;
                var hash = sha256.ComputeHash(ms);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // Modified CopyFiles method to show size and average transfer speed
        static void CopyFiles(MediaDevice device, List<(string sourcePath, string fileName, long size)> videoFiles, string destinationRoot, CopyProgress progress)
        {
            foreach (var (sourcePath, fileName, size, index) in videoFiles.Select((file, index) => (file.sourcePath, file.fileName, file.size, index)))
            {
                var destPath = Path.Combine(destinationRoot, fileName);

                // If file exists and size matches, skip copying
                if (IsAlreadyCopied(destPath, size))
                {
                    Console.WriteLine($"Skipped (already copied): {fileName} ({index + 1}/{videoFiles.Count}, {((index + 1) * 100 / videoFiles.Count):F2}%)");
                    Console.Out.Flush();
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
                {
                    try
                    {
                        using (var destStream = File.Create(destPath))
                        {
                            device.DownloadFile(sourcePath, destStream);
                        }
                        break;
                    }
                    catch (Exception ex) when (attempt < MaxDownloadAttempts)
                    {
                        Console.WriteLine($"⚠️ Error reading {fileName} from device (attempt {attempt}/{MaxDownloadAttempts}): {ex.Message}. Retrying...");
                        Console.Out.Flush();
                        System.Threading.Thread.Sleep(DownloadRetryDelayMs);
                    }
                }
                sw.Stop();

                double sizeMB = size / (1024.0 * 1024.0);
                double seconds = sw.Elapsed.TotalSeconds;
                double speedMBps = seconds > 0 ? sizeMB / seconds : 0;

                progress.BytesCopied += size;
                progress.BytesRemaining = Math.Max(0, progress.BytesRemaining - size);
                progress.TransferTime += sw.Elapsed;

                Console.WriteLine($"Copied: {fileName} ({index + 1}/{videoFiles.Count}, {((index + 1) * 100 / videoFiles.Count):F2}%) | Size: {sizeMB:F2} MB | Speed: {speedMBps:F2} MB/s");
                Console.WriteLine($"   ⏱️ {progress.Describe()}");
                Console.Out.Flush();
            }
        }

        static bool IsAlreadyCopied(string destPath, long size)
        {
            return File.Exists(destPath) && new FileInfo(destPath).Length == size;
        }

        // Formats a duration as h:mm:ss (hours can exceed 24)
        static string FormatDuration(TimeSpan duration)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        // Formats a local time, adding the date if it isn't today
        static string FormatLocalTime(DateTime time)
        {
            return time.Date == DateTime.Today ? time.ToString("T") : time.ToString("ddd d MMM, T");
        }

        // Tracks the overall copy across retry and reconnect passes, for the ETA and the final summary
        class CopyProgress
        {
            public DateTime StartTime = DateTime.Now;
            public long BytesCopied;
            public long BytesRemaining;
            public TimeSpan TransferTime;

            // Average speed over all bytes copied so far, in bytes per second
            public double AverageSpeed => TransferTime.TotalSeconds > 0 ? BytesCopied / TransferTime.TotalSeconds : 0;

            public string Describe()
            {
                var elapsed = DateTime.Now - StartTime;
                if (AverageSpeed <= 0)
                {
                    return $"Elapsed: {FormatDuration(elapsed)}";
                }
                var remaining = TimeSpan.FromSeconds(BytesRemaining / AverageSpeed);
                return $"Elapsed: {FormatDuration(elapsed)} | Remaining: ~{FormatDuration(remaining)} | Finishes: ~{FormatLocalTime(DateTime.Now + remaining)}";
            }
        }

        // Method to verify that copied files match the originals in size
        static void VerifyFiles(List<(string sourcePath, string fileName, long size)> videoFiles, string destinationRoot, out List<string> verified, out List<string> failed)
        {
            Console.WriteLine("\n🔍 Verifying copied files...");
            verified = new List<string>();
            failed = new List<string>();

            foreach (var (sourcePath, fileName, size) in videoFiles)
            {
                var destPath = Path.Combine(destinationRoot, fileName);
                // Check if the file exists and its size matches the original
                if (File.Exists(destPath) && new FileInfo(destPath).Length == size)
                {
                    verified.Add(sourcePath); // Add to verified list
                }
                else
                {
                    failed.Add(fileName); // Add to failed list
                }
            }

            // Display verification results
            Console.WriteLine($"\n✅ {verified.Count} file(s) verified.");
            if (failed.Count > 0)
            {
                Console.WriteLine("⚠️ Some files failed to verify:");
                failed.ForEach(f => Console.WriteLine($" - {f}"));
            }
        }

        // Method to check if there is sufficient space on the destination drive
        static bool CheckDriveSpace(string destinationPath, long requiredBytes)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(destinationPath));
                long availableBytes = driveInfo.AvailableFreeSpace;
                double requiredGB = requiredBytes / (1024.0 * 1024.0 * 1024.0);
                double availableGB = availableBytes / (1024.0 * 1024.0 * 1024.0);

                Console.WriteLine($"\n💾 Space Check:");
                Console.WriteLine($"   Required: {requiredGB:F2} GB");
                Console.WriteLine($"   Available: {availableGB:F2} GB");

                if (availableBytes < requiredBytes)
                {
                    Console.WriteLine($"   ⚠️ Insufficient space! Short by {(requiredBytes - availableBytes) / (1024.0 * 1024.0 * 1024.0):F2} GB");
                    return false;
                }
                else
                {
                    Console.WriteLine($"   ✅ Sufficient space available.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Could not check drive space: {ex.Message}");
                return true; // Assume sufficient space if check fails
            }
        }

        // Method to handle deletion of successfully copied files from the iPhone
        // deleteWithoutAsking is only set when --autodelete was given and every file verified
        static void HandleDeletion(MediaDevice device, List<string> verified, bool deleteWithoutAsking)
        {
            if (verified.Count > 0)
            {
                bool delete;
                if (deleteWithoutAsking)
                {
                    Console.WriteLine("\n🗑️ All files verified; deleting them from the device (--autodelete).");
                    delete = true;
                }
                else
                {
                    // Prompt the user to confirm deletion
                    Console.Write("\n❓ Delete successfully copied files from iPhone? (Y/N): ");
                    var response = Console.ReadLine();
                    delete = response?.Trim().ToUpper() == "Y";
                }
                if (delete)
                {
                    Console.WriteLine("🗑️ Deleting files...");
                    foreach (var path in verified)
                    {
                        // Delete the file from the iPhone
                        device.DeleteFile(path);
                        Console.WriteLine($"Deleted: {Path.GetFileName(path)}");
                        Console.Out.Flush();
                    }
                    Beep(3);
                }
                else
                {
                    Console.WriteLine("⏭️ Skipped deletion.");
                }
            }
        }
    }
}
