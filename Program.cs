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

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            try
            {
                // Parse command-line arguments for destination directory and device type
                string destinationRoot = null;
                string deviceType = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if ((args[i].Equals("--dest", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/dest", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                    {
                        destinationRoot = args[i + 1];
                        i++;
                        continue;
                    }
                    if ((args[i].Equals("--device", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/device", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                    {
                        deviceType = args[i + 1].Trim().ToLowerInvariant();
                        i++;
                        continue;
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

                // Create the destination directory if it doesn't exist
                Directory.CreateDirectory(destinationRoot);

                // List all detected devices and their friendly names
                var devices = MediaDevice.GetDevices();
                Console.WriteLine($"Devices found: {devices.Count()}");
                foreach (var mediaDevice in devices)
                {
                    Console.WriteLine($"Device: {mediaDevice.FriendlyName}");
                }

                // Find the connected device by its friendly name
                MediaDevice device = null;
                if (deviceType == "iphone")
                {
                    device = devices.FirstOrDefault(d => d.FriendlyName.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                else if (deviceType == "pixel")
                {
                    device = devices.FirstOrDefault(d => d.FriendlyName.IndexOf("Pixel", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                if (device == null)
                {
                    Console.WriteLine($"❌ {deviceType.First().ToString().ToUpper() + deviceType.Substring(1)} not found.");
                    return; // Exit if no device is found
                }

                // Connect to the device BEFORE accessing files
                try
                {
                    device.Connect();
                    Console.WriteLine($"✅ Connected to {deviceType} device.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to connect: {ex.Message}");
                    return;
                }

                if (!device.IsConnected)
                {
                    Console.WriteLine("❌ Device is not connected.");
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

                // Prompt the user to commence copying files
                Console.Write("❓ Do you want to commence copying files? (Y/N): ");
                var startResponse = Console.ReadLine();
                if (startResponse == null || !startResponse.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("⏹️ Operation cancelled by user.");
                    return;
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

                // Variables to track verification results
                List<string> verified = new List<string>();
                List<string> failed = new List<string>();

                // Copy, then verify; if any files failed verification, retry just those files
                var filesToCopy = videoFiles;
                for (int round = 1; round <= MaxVerificationRounds; round++)
                {
                    try
                    {
                        CopyFiles(device, filesToCopy, destinationRoot);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n⚠️ Transfer was interrupted: {ex.Message}");
                    }

                    // Verify that the copied files match the originals
                    VerifyFiles(videoFiles, destinationRoot, out verified, out failed);

                    if (failed.Count == 0 || round == MaxVerificationRounds)
                    {
                        break;
                    }

                    Console.WriteLine($"\n🔁 {failed.Count} file(s) failed verification. Retrying (attempt {round + 1}/{MaxVerificationRounds})...");
                    filesToCopy = videoFiles.Where(f => failed.Contains(f.fileName)).ToList();
                }

                // Handle deletion of successfully copied files based on user input
                HandleDeletion(device, verified);

                // Disconnect the device
                device.Disconnect();
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
        static void CopyFiles(MediaDevice device, List<(string sourcePath, string fileName, long size)> videoFiles, string destinationRoot)
        {
            foreach (var (sourcePath, fileName, size, index) in videoFiles.Select((file, index) => (file.sourcePath, file.fileName, file.size, index)))
            {
                var destPath = Path.Combine(destinationRoot, fileName);

                // If file exists and size matches, skip copying
                if (File.Exists(destPath))
                {
                    var destFileInfo = new FileInfo(destPath);
                    if (destFileInfo.Length == size)
                    {
                        Console.WriteLine($"Skipped (already copied): {fileName} ({index + 1}/{videoFiles.Count}, {((index + 1) * 100 / videoFiles.Count):F2}%)");
                        Console.Out.Flush();
                        continue;
                    }
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

                Console.WriteLine($"Copied: {fileName} ({index + 1}/{videoFiles.Count}, {((index + 1) * 100 / videoFiles.Count):F2}%) | Size: {sizeMB:F2} MB | Speed: {speedMBps:F2} MB/s");
                Console.Out.Flush();
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
