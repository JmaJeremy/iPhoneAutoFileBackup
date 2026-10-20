<#
.SYNOPSIS
    Installs iPhoneVideoBackup and creates a scheduled task that launches it when an iPhone is connected.

.DESCRIPTION
    Copies the built executable and the start_iphone_copy.bat wrapper to Program Files and registers
    a scheduled task, triggered by the Windows MTP driver event that is logged when a device is
    connected over USB. The task runs the wrapper, which counts down for 10 seconds (any key cancels)
    and then starts the backup without asking for confirmation.
    Must be run from an elevated (Administrator) PowerShell prompt.

.PARAMETER Dest
    Backup destination folder passed to the program. Prompted for if omitted.

.PARAMETER TaskName
    Name of the scheduled task to create.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Dest D:\Backup\iphone
#>
param(
    [string]$Dest,
    [string]$TaskName = "backup iphone"
)

$ErrorActionPreference = "Stop"

$AppName    = "iPhoneVideoBackup"
$ExeName    = "$AppName.exe"
$WrapperName = "start_iphone_copy.bat"
$InstallDir = Join-Path $env:ProgramFiles $AppName
$EventLog   = "Microsoft-Windows-WPD-MTPClassDriver/Operational"

# Writing to Program Files and registering an elevated task both need admin rights
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This script must be run as Administrator. Open PowerShell with 'Run as administrator' and try again."
    exit 1
}

# Check whether the scheduled task already exists before changing anything
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Write-Host "Scheduled task '$TaskName' already exists:"
    foreach ($action in $existingTask.Actions) {
        Write-Host "  $($action.Execute) $($action.Arguments)"
    }
    $answer = Read-Host "Replace it? (Y/N)"
    if ($answer.Trim() -ne "Y") {
        Write-Host "Installation cancelled. Nothing was changed."
        exit 0
    }
    # A running instance locks the executable, which would block the build and the copy
    if ($existingTask.State -eq "Running") {
        Write-Host "Stopping the running task..."
        Stop-ScheduledTask -TaskName $TaskName
        # Stopping the task ends the wrapper, but the program it started may still be running
        Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

# Build a fresh release so the installed program matches the current source;
# fall back to an existing release build if the dotnet CLI is not available
$sourceDir = Join-Path $PSScriptRoot "bin\Release\net48"
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host "Building release..."
    & dotnet build (Join-Path $PSScriptRoot "$AppName.csproj") -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed."
        exit 1
    }
} else {
    Write-Host "The 'dotnet' CLI was not found; using the existing release build."
}
if (-not (Test-Path (Join-Path $sourceDir $ExeName))) {
    Write-Host "$ExeName was not found in $sourceDir. Build the project first (see README.md)."
    exit 1
}

# Prompt for the backup destination if it was not supplied
if ([string]::IsNullOrWhiteSpace($Dest)) {
    $Dest = Read-Host "Enter the folder to back up to (e.g. D:\Backup\iphone)"
}
$Dest = $Dest.Trim().Trim('"')
if ($Dest -notmatch '^[A-Za-z]:\\') {
    Write-Host "Please provide an absolute path on a local drive (e.g. D:\Backup\iphone)."
    exit 1
}
# A trailing backslash would escape the closing quote on the command line
$Dest = $Dest.TrimEnd('\')
if ($Dest -match '^[A-Za-z]:$') {
    $Dest = "$Dest\"
}
$drive = $Dest.Substring(0, 3)
if (-not (Test-Path $drive)) {
    Write-Host "Warning: drive $drive is not currently available. The backup will fail until it is connected."
}

# Install the program to Program Files
Write-Host "Installing from $sourceDir to $InstallDir..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem -Path $sourceDir -File | Copy-Item -Destination $InstallDir -Force
Copy-Item -Path (Join-Path $PSScriptRoot $WrapperName) -Destination $InstallDir -Force
$wrapperPath = Join-Path $InstallDir $WrapperName

# The task is triggered by an event in this log, so make sure the log is enabled
$log = Get-WinEvent -ListLog $EventLog
if (-not $log.IsEnabled) {
    Write-Host "Enabling event log $EventLog..."
    $log.IsEnabled = $true
    $log.SaveChanges()
}

# Build the task definition. The task runs the wrapper through cmd.exe; /y skips the program's
# "commence copying?" prompt, since the wrapper's countdown already gives a chance to cancel.
if ($Dest -match '\s') {
    $programArguments = "/dest `"$Dest`" /device iphone /y"
} else {
    $programArguments = "/dest $Dest /device iphone /y"
}
# cmd /c strips the outer pair of quotes, leaving the quoted wrapper path and its arguments
$command   = Join-Path $env:SystemRoot "System32\cmd.exe"
$arguments = "/c `"`"$wrapperPath`" $programArguments`""
$xmlCommand   = [Security.SecurityElement]::Escape($command)
$xmlArguments = [Security.SecurityElement]::Escape($arguments)
$xmlAuthor    = [Security.SecurityElement]::Escape($identity.Name)
$userSid      = $identity.User.Value

$taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>$xmlAuthor</Author>
    <Description>Launches $AppName when an iPhone is connected over USB.</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="$EventLog"&gt;&lt;Select Path="$EventLog"&gt;*[System[Provider[@Name='Microsoft-Windows-WPD-MTPClassDriver'] and EventID=1005]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$userSid</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>true</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$xmlCommand</Command>
      <Arguments>$xmlArguments</Arguments>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $TaskName -Xml $taskXml -Force | Out-Null

Write-Host ""
Write-Host "Installed."
Write-Host "  Program: $InstallDir"
Write-Host "  Task:    $TaskName"
Write-Host "  Runs:    `"$wrapperPath`" $programArguments"
Write-Host "Connect an iPhone over USB to start a backup."
