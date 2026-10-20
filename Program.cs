using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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

        // Held for the life of the process so a second copy can tell one is already running
        private const string InstanceMutexName = @"Global\iPhoneVideoBackup";
        private static System.Threading.Mutex instanceMutex;

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Show usage and exit straight away, without waiting for Enter
            if (args.Any(a => IsOption(a, "help") || a.Equals("-h", StringComparison.OrdinalIgnoreCase) || a == "/?"))
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

            try
            {
                // Parse command-line arguments
                string destinationRoot = null;
                string deviceType = null;
                bool autoConfirm = false;
                int reconnectDelaySeconds = DefaultReconnectDelaySeconds;
                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (IsOption(arg, "dest") || IsOption(arg, "device") || IsOption(arg, "retry-delay"))
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
                        else if (!int.TryParse(value, out reconnectDelaySeconds) || reconnectDelaySeconds < 0 || reconnectDelaySeconds > MaxReconnectDelaySeconds)
                        {
                            Console.WriteLine($"❌ {arg} must be a number of seconds between 0 and {MaxReconnectDelaySeconds}.");
                            return;
                        }
                    }
                    else if (IsOption(arg, "yes") || arg.Equals("-y", StringComparison.OrdinalIgnoreCase) || arg.Equals("/y", StringComparison.OrdinalIgnoreCase))
                    {
                        autoConfirm = true;
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

                if (device != null)
                {
                    // Handle deletion of successfully copied files based on user input
                    HandleDeletion(device, verified);

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
                // Wait for the user to press Enter before closing the program
                Console.WriteLine("\nPress Enter to exit...");
                Console.ReadLine();
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

        // Matches an option written as --name or /name
        static bool IsOption(string arg, string name)
        {
            return arg.Equals("--" + name, StringComparison.OrdinalIgnoreCase) || arg.Equals("/" + name, StringComparison.OrdinalIgnoreCase);
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: iPhoneVideoBackup.exe [--dest <folder>] [--device iphone|pixel] [-y] [--retry-delay <seconds>]");
            Console.WriteLine();
            Console.WriteLine("Backs up photos and videos from a USB-connected phone to <folder>\\yyyy-MM-dd,");
            Console.WriteLine("verifies the copies, and optionally deletes the originals from the phone.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --dest <folder>          Absolute path of the backup folder. Prompted for if omitted.");
            Console.WriteLine("  --device iphone|pixel    Type of phone to back up. Prompted for if omitted.");
            Console.WriteLine("  -y, --yes                Start copying without asking, as long as there is enough free space.");
            Console.WriteLine("  --retry-delay <seconds>  Seconds to wait before reconnecting when the connection to the phone");
            Console.WriteLine($"                           is lost (default {DefaultReconnectDelaySeconds}). Gives up after {MaxReconnectAttempts} failed attempts.");
            Console.WriteLine("  -h, --help               Show this help.");
            Console.WriteLine();
            Console.WriteLine("Options can also be written with a slash, e.g. /dest or /y.");
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
        static void HandleDeletion(MediaDevice device, List<string> verified)
        {
            if (verified.Count > 0)
            {
                // Prompt the user to confirm deletion
                Console.Write("\n❓ Delete successfully copied files from iPhone? (Y/N): ");
                var response = Console.ReadLine();
                if (response?.Trim().ToUpper() == "Y")
                {
                    Console.WriteLine("🗑️ Deleting files...");
                    foreach (var path in verified)
                    {
                        // Delete the file from the iPhone
                        device.DeleteFile(path);
                        Console.WriteLine($"Deleted: {Path.GetFileName(path)}");
                        Console.Out.Flush();
                    }
                }
                else
                {
                    Console.WriteLine("⏭️ Skipped deletion.");
                }
            }
        }
    }
}
