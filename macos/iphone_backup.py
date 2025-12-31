#!/usr/bin/env python3
"""
iPhone/Android Media Backup Tool for macOS
Equivalent to the Windows C# iPhoneVideoBackup program

This script backs up media files from iOS devices (via libimobiledevice) 
or Android devices (via adb) to a local directory, verifies the transfers,
and optionally deletes the files from the device.
"""

import os
import sys
import argparse
import subprocess
import hashlib
import shutil
from pathlib import Path
from datetime import datetime
from typing import List, Tuple, Optional


# Supported file extensions
SUPPORTED_EXTENSIONS = ['.mov', '.mp4', '.avi', '.jpg', '.jpeg', '.png', '.heic']


class DeviceBackup:
    """Base class for device backup operations"""
    
    def __init__(self, destination: str, device_type: str):
        self.destination = destination
        self.device_type = device_type
        self.verified_files = []
        self.failed_files = []
        
    def check_disk_space(self, required_bytes: int) -> bool:
        """Check if there's sufficient space on the destination drive"""
        try:
            stat = shutil.disk_usage(self.destination)
            available_bytes = stat.free
            required_gb = required_bytes / (1024**3)
            available_gb = available_bytes / (1024**3)
            
            print(f"\n💾 Space Check:")
            print(f"   Required: {required_gb:.2f} GB")
            print(f"   Available: {available_gb:.2f} GB")
            
            if available_bytes < required_bytes:
                shortage_gb = (required_bytes - available_bytes) / (1024**3)
                print(f"   ⚠️  Insufficient space! Short by {shortage_gb:.2f} GB")
                return False
            else:
                print(f"   ✅ Sufficient space available.")
                return True
        except Exception as e:
            print(f"⚠️  Could not check drive space: {e}")
            return True  # Assume sufficient space if check fails
    
    def compute_file_checksum(self, file_path: str) -> str:
        """Compute SHA256 checksum of a local file"""
        sha256 = hashlib.sha256()
        with open(file_path, 'rb') as f:
            for chunk in iter(lambda: f.read(4096), b''):
                sha256.update(chunk)
        return sha256.hexdigest()
    
    def verify_files(self, files: List[Tuple[str, str, int]]) -> Tuple[List[str], List[str]]:
        """Verify that copied files match the originals in size"""
        print("\n🔍 Verifying copied files...")
        verified = []
        failed = []
        
        for source_path, file_name, size in files:
            dest_path = os.path.join(self.destination, file_name)
            if os.path.exists(dest_path) and os.path.getsize(dest_path) == size:
                verified.append(source_path)
            else:
                failed.append(file_name)
        
        print(f"\n✅ {len(verified)} file(s) verified.")
        if failed:
            print("⚠️  Some files failed to verify:")
            for f in failed:
                print(f" - {f}")
        
        self.verified_files = verified
        self.failed_files = failed
        return verified, failed


class iPhoneBackup(DeviceBackup):
    """iPhone backup using libimobiledevice (ifuse/idevice tools)"""
    
    def __init__(self, destination: str):
        super().__init__(destination, "iPhone")
        self.mount_point = None
        
    def check_dependencies(self) -> bool:
        """Check if required tools are installed"""
        try:
            # Check for ideviceinfo
            subprocess.run(['ideviceinfo', '-s'], 
                         capture_output=True, check=True, timeout=5)
            return True
        except (subprocess.CalledProcessError, FileNotFoundError, subprocess.TimeoutExpired):
            print("❌ libimobiledevice tools not found.")
            print("   Install with: brew install libimobiledevice ifuse")
            return False
    
    def is_device_connected(self) -> bool:
        """Check if an iPhone is connected"""
        try:
            result = subprocess.run(['ideviceinfo', '-s'], 
                                  capture_output=True, text=True, timeout=5)
            return result.returncode == 0
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            return False
    
    def mount_device(self) -> Optional[str]:
        """Mount iPhone using ifuse"""
        try:
            # Create temporary mount point
            mount_point = f"/tmp/iphone_mount_{os.getpid()}"
            os.makedirs(mount_point, exist_ok=True)
            
            # Mount the device
            result = subprocess.run(['ifuse', mount_point], 
                                  capture_output=True, text=True, timeout=10)
            
            if result.returncode == 0:
                self.mount_point = mount_point
                print(f"✅ Connected to iPhone device.")
                return mount_point
            else:
                print(f"❌ Failed to mount iPhone: {result.stderr}")
                return None
        except Exception as e:
            print(f"❌ Failed to mount device: {e}")
            return None
    
    def unmount_device(self):
        """Unmount the iPhone"""
        if self.mount_point:
            try:
                subprocess.run(['umount', self.mount_point], timeout=10)
                os.rmdir(self.mount_point)
            except Exception as e:
                print(f"⚠️  Error unmounting: {e}")
    
    def find_media_files(self) -> List[Tuple[str, str, int]]:
        """Find all media files in DCIM folder"""
        if not self.mount_point:
            return []
        
        dcim_path = os.path.join(self.mount_point, "DCIM")
        if not os.path.exists(dcim_path):
            print(f"⚠️  DCIM folder not found at {dcim_path}")
            return []
        
        media_files = []
        
        # Walk through DCIM directory
        for root, dirs, files in os.walk(dcim_path):
            for file in files:
                ext = os.path.splitext(file)[1].lower()
                if ext in SUPPORTED_EXTENSIONS:
                    full_path = os.path.join(root, file)
                    try:
                        size = os.path.getsize(full_path)
                        media_files.append((full_path, file, size))
                    except OSError as e:
                        print(f"⚠️  Could not access {file}: {e}")
        
        return sorted(media_files, key=lambda x: x[1])
    
    def copy_files(self, files: List[Tuple[str, str, int]]):
        """Copy files from iPhone to destination"""
        total_files = len(files)
        
        for idx, (source_path, file_name, size) in enumerate(files, 1):
            dest_path = os.path.join(self.destination, file_name)
            
            # Skip if file exists and size matches
            if os.path.exists(dest_path) and os.path.getsize(dest_path) == size:
                progress = (idx * 100) / total_files
                print(f"Skipped (already copied): {file_name} ({idx}/{total_files}, {progress:.2f}%)")
                continue
            
            # Copy file with timing
            import time
            start_time = time.time()
            
            try:
                shutil.copy2(source_path, dest_path)
                elapsed = time.time() - start_time
                
                size_mb = size / (1024 * 1024)
                speed_mbps = size_mb / elapsed if elapsed > 0 else 0
                progress = (idx * 100) / total_files
                
                print(f"Copied: {file_name} ({idx}/{total_files}, {progress:.2f}%) | "
                      f"Size: {size_mb:.2f} MB | Speed: {speed_mbps:.2f} MB/s")
                sys.stdout.flush()
            except Exception as e:
                print(f"❌ Error copying {file_name}: {e}")
    
    def delete_files(self, file_paths: List[str]):
        """Delete files from iPhone"""
        print("🗑️  Deleting files...")
        for path in file_paths:
            try:
                os.remove(path)
                print(f"Deleted: {os.path.basename(path)}")
                sys.stdout.flush()
            except Exception as e:
                print(f"❌ Error deleting {os.path.basename(path)}: {e}")


class AndroidBackup(DeviceBackup):
    """Android backup using adb"""
    
    def __init__(self, destination: str):
        super().__init__(destination, "Android")
    
    def check_dependencies(self) -> bool:
        """Check if adb is installed"""
        try:
            subprocess.run(['adb', 'version'], 
                         capture_output=True, check=True, timeout=5)
            return True
        except (subprocess.CalledProcessError, FileNotFoundError, subprocess.TimeoutExpired):
            print("❌ adb not found.")
            print("   Install Android Platform Tools from: https://developer.android.com/studio/releases/platform-tools")
            return False
    
    def is_device_connected(self) -> bool:
        """Check if an Android device is connected"""
        try:
            result = subprocess.run(['adb', 'devices'], 
                                  capture_output=True, text=True, timeout=5)
            lines = result.stdout.strip().split('\n')
            # Check if there's at least one device (more than just the header line)
            return len(lines) > 1 and 'device' in lines[1]
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            return False
    
    def find_media_files(self) -> List[Tuple[str, str, int]]:
        """Find all media files in DCIM folder using adb"""
        dcim_path = "/sdcard/DCIM"
        media_files = []
        
        try:
            # List all files recursively in DCIM
            result = subprocess.run(
                ['adb', 'shell', f'find {dcim_path} -type f'],
                capture_output=True, text=True, timeout=30
            )
            
            if result.returncode != 0:
                print(f"⚠️  Could not access DCIM folder")
                return []
            
            file_paths = result.stdout.strip().split('\n')
            
            for file_path in file_paths:
                file_path = file_path.strip()
                if not file_path:
                    continue
                
                file_name = os.path.basename(file_path)
                ext = os.path.splitext(file_name)[1].lower()
                
                if ext in SUPPORTED_EXTENSIONS:
                    # Get file size
                    size_result = subprocess.run(
                        ['adb', 'shell', f'stat -c %s "{file_path}"'],
                        capture_output=True, text=True, timeout=5
                    )
                    
                    if size_result.returncode == 0:
                        try:
                            size = int(size_result.stdout.strip())
                            media_files.append((file_path, file_name, size))
                        except ValueError:
                            pass
            
            return sorted(media_files, key=lambda x: x[1])
            
        except Exception as e:
            print(f"❌ Error finding media files: {e}")
            return []
    
    def copy_files(self, files: List[Tuple[str, str, int]]):
        """Copy files from Android to destination using adb pull"""
        total_files = len(files)
        
        for idx, (source_path, file_name, size) in enumerate(files, 1):
            dest_path = os.path.join(self.destination, file_name)
            
            # Skip if file exists and size matches
            if os.path.exists(dest_path) and os.path.getsize(dest_path) == size:
                progress = (idx * 100) / total_files
                print(f"Skipped (already copied): {file_name} ({idx}/{total_files}, {progress:.2f}%)")
                continue
            
            # Copy file with timing
            import time
            start_time = time.time()
            
            try:
                result = subprocess.run(
                    ['adb', 'pull', source_path, dest_path],
                    capture_output=True, text=True, timeout=300
                )
                
                if result.returncode == 0:
                    elapsed = time.time() - start_time
                    size_mb = size / (1024 * 1024)
                    speed_mbps = size_mb / elapsed if elapsed > 0 else 0
                    progress = (idx * 100) / total_files
                    
                    print(f"Copied: {file_name} ({idx}/{total_files}, {progress:.2f}%) | "
                          f"Size: {size_mb:.2f} MB | Speed: {speed_mbps:.2f} MB/s")
                    sys.stdout.flush()
                else:
                    print(f"❌ Error copying {file_name}: {result.stderr}")
            except Exception as e:
                print(f"❌ Error copying {file_name}: {e}")
    
    def delete_files(self, file_paths: List[str]):
        """Delete files from Android device"""
        print("🗑️  Deleting files...")
        for path in file_paths:
            try:
                result = subprocess.run(
                    ['adb', 'shell', f'rm "{path}"'],
                    capture_output=True, text=True, timeout=10
                )
                if result.returncode == 0:
                    print(f"Deleted: {os.path.basename(path)}")
                    sys.stdout.flush()
                else:
                    print(f"❌ Error deleting {os.path.basename(path)}")
            except Exception as e:
                print(f"❌ Error deleting {os.path.basename(path)}: {e}")


def main():
    """Main entry point"""
    parser = argparse.ArgumentParser(
        description='Backup media files from iPhone or Android devices on macOS'
    )
    parser.add_argument('--dest', type=str, help='Destination directory for backups')
    parser.add_argument('--device', type=str, choices=['iphone', 'pixel', 'android'],
                       help='Device type (iphone, pixel, or android)')
    
    args = parser.parse_args()
    
    try:
        # Get destination directory
        destination_root = args.dest
        if not destination_root:
            destination_root = input("📁 Enter destination directory (required): ").strip()
            if not destination_root:
                print("❌ Destination directory is required. Exiting.")
                return
        
        # Validate destination path
        destination_root = os.path.abspath(os.path.expanduser(destination_root))
        
        # Get device type
        device_type = args.device
        if not device_type:
            print("Select device type:")
            print("  1. iPhone")
            print("  2. Pixel/Android")
            choice = input("Enter 1 or 2: ").strip()
            if choice == "1":
                device_type = "iphone"
            elif choice == "2":
                device_type = "android"
            else:
                print("❌ Invalid device type selection. Exiting.")
                return
        
        # Normalize device type
        if device_type == "pixel":
            device_type = "android"
        
        # Create date-based subdirectory
        today = datetime.today().strftime("%Y-%m-%d")
        destination_root = os.path.join(destination_root, today)
        os.makedirs(destination_root, exist_ok=True)
        
        # Create appropriate backup handler
        if device_type == "iphone":
            backup = iPhoneBackup(destination_root)
        else:
            backup = AndroidBackup(destination_root)
        
        # Check dependencies
        if not backup.check_dependencies():
            return
        
        # Check if device is connected
        if not backup.is_device_connected():
            print(f"❌ {backup.device_type} not found.")
            return
        
        # Mount device if iPhone
        if isinstance(backup, iPhoneBackup):
            if not backup.mount_device():
                return
        
        try:
            # Ask user to commence
            response = input("❓ Do you want to commence copying files? (Y/N): ").strip().upper()
            if response != "Y":
                print("⏹️  Operation cancelled by user.")
                return
            
            print(f"\n📂 Starting to copy files to {destination_root}...\n")
            
            # Find media files
            media_files = backup.find_media_files()
            
            if not media_files:
                print("⚠️  No supported files found.")
                return
            
            # Check disk space
            total_size = sum(size for _, _, size in media_files)
            if not backup.check_disk_space(total_size):
                response = input("\n❓ Insufficient space on destination drive. "
                               "Do you still want to continue? (Y/N): ").strip().upper()
                if response != "Y":
                    print("⏹️  Operation cancelled by user.")
                    return
            
            # Copy files
            try:
                backup.copy_files(media_files)
            except Exception as e:
                print(f"\n⚠️  Transfer was interrupted: {e}")
            finally:
                # Verify files
                verified, failed = backup.verify_files(media_files)
                
                # Handle deletion
                if verified:
                    response = input(f"\n❓ Delete successfully copied files from {backup.device_type}? "
                                   "(Y/N): ").strip().upper()
                    if response == "Y":
                        backup.delete_files(verified)
                    else:
                        print("⏭️  Skipped deletion.")
            
            print("\n🎉 Done.")
            
        finally:
            # Cleanup
            if isinstance(backup, iPhoneBackup):
                backup.unmount_device()
    
    except Exception as e:
        print(f"❌ An error occurred: {e}")
    finally:
        input("\nPress Enter to exit...")


if __name__ == "__main__":
    main()
