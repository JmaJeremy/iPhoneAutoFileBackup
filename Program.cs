using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using MediaDevices;

namespace iPhoneVideoBackup
{
    class Program
    {
        // Define a constant array of file extensions to handle
        private static readonly string[] SupportedExtensions = { "*.MOV", "*.MP4", "*.AVI" };

        static void Main(string[] args)
        {
            try
            {
                // Get today's date in "yyyy-MM-dd" format for folder naming
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                var destinationRoot = Path.Combine("H:\\", "Steven", "iphone", today);

                // Create the destination directory if it doesn't exist
                Directory.CreateDirectory(destinationRoot);

                // Find the connected iPhone device by its friendly name
                var device = MediaDevice.GetDevices().FirstOrDefault(d => d.FriendlyName.Contains("iPhone"));
                if (device == null)
                {
                    Console.WriteLine("❌ iPhone not found.");
                    return; // Exit if no iPhone is found
                }

                // Path to the DCIM folder where photos and videos are stored
                var dcimPath = @"\Internal Storage\DCIM";
                var videoFiles = new List<(string sourcePath, string fileName, long size)>();

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

                // Connect to the iPhone device
                device.Connect();

                // Copy video files to the destination directory
                CopyFiles(device, videoFiles, destinationRoot);

                // Verify that the copied files match the originals
                VerifyFiles(videoFiles, destinationRoot, out var verified, out var failed);

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

        // Method to copy video files from the iPhone to the destination directory
        static void CopyFiles(MediaDevice device, List<(string sourcePath, string fileName, long size)> videoFiles, string destinationRoot)
        {
            foreach (var (sourcePath, fileName, size, index) in videoFiles.Select((file, index) => (file.sourcePath, file.fileName, file.size, index)))
            {
                var destPath = Path.Combine(destinationRoot, fileName);
                using (var sourceStream = new MemoryStream())
                {
                    // Download the file from the iPhone to a memory stream
                    device.DownloadFile(sourcePath, sourceStream);
                    sourceStream.Position = 0; // Reset stream position before copying

                    // Create the destination file and copy the contents from the memory stream
                    using (var destStream = File.Create(destPath))
                    {
                        sourceStream.CopyTo(destStream);
                    }
                }
                // Update the console output to include the number of files copied and percentage
                Console.WriteLine($"Copied: {fileName} ({index + 1}/{videoFiles.Count}, {((index + 1) * 100 / videoFiles.Count):F2}%)");
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
