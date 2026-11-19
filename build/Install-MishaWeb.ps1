<#
.SYNOPSIS
    All-in-One Installer for MishaWeb Browser Engine.

.DESCRIPTION
    Downloads the entire MishaWeb repository from GitHub, installs and sets up
    the application in the user's local programs directory, creates Desktop and Start Menu
    shortcuts, and displays an 'Installation Finished' completion dialog with a checkbox
    to immediately launch MishaWeb.exe.
#>

[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\MishaWeb'),
    [string]$RepoUrl = 'https://github.com/MishaelOliva/browser-engine',
    [string]$Branch = 'main',
    [switch]$ForceDownload,
    [switch]$NoGui
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'Continue'

# Enable TLS 1.2 / 1.3
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "             MISHAWEB ALL-IN-ONE INSTALLER SETUP                 " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "Target Installation Directory: $InstallPath" -ForegroundColor Gray

# 1. Determine Source (Local or GitHub Download)
$scriptDir = $PSScriptRoot
$repoRootCandidate = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$isLocalRepo = (Test-Path (Join-Path $repoRootCandidate 'desktop\MishaWeb.csproj'))

$script:InstallDirPrefix = [System.IO.Path]::GetFullPath($InstallPath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$installFullPath = [System.IO.Path]::GetFullPath($InstallPath).TrimEnd('\', '/')

$stagingDir = Join-Path $env:TEMP "MishaWeb_Install_$([Guid]::NewGuid().ToString('N'))"
$backupDir = $null
$wshShell = $null
$desktopShortcut = $null
$startMenuShortcut = $null
$form = $null
$disposables = New-Object System.Collections.Generic.List[System.IDisposable]

New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
    # Fail fast before mutating anything if MishaWeb is currently running from the install target.
    $runningFromTarget = @(Get-Process -Name 'MishaWeb' -ErrorAction SilentlyContinue | Where-Object {
        try {
            $exePath = $_.MainModule.FileName
            $null -ne $exePath -and $exePath.StartsWith($script:InstallDirPrefix, [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
    if ($runningFromTarget.Count -gt 0) {
        $pids = ($runningFromTarget | ForEach-Object { $_.Id }) -join ', '
        throw "MishaWeb is currently running from '$installFullPath' (PID: $pids). Close it before installing."
    }

    if ($isLocalRepo -and (-not $ForceDownload)) {
        Write-Host "`n[1/4] Detected local MishaWeb repository source at:" -ForegroundColor Green
        Write-Host "      $repoRootCandidate" -ForegroundColor Gray
        $sourceDir = $repoRootCandidate
    }
    else {
        Write-Host "`n[1/4] Downloading MishaWeb repository from GitHub..." -ForegroundColor Green
        $zipUrl = "$RepoUrl/archive/refs/heads/$Branch.zip"
        $fallbackZipUrl = "$RepoUrl/archive/refs/heads/master.zip"
        $zipFile = Join-Path $stagingDir 'mishaweb-source.zip'

        Write-Host "      Fetching from: $zipUrl" -ForegroundColor Gray
        try {
            Invoke-WebRequest -Uri $zipUrl -OutFile $zipFile -UseBasicParsing
        }
        catch {
            Write-Host "      Retrying with fallback master branch: $fallbackZipUrl" -ForegroundColor Yellow
            Invoke-WebRequest -Uri $fallbackZipUrl -OutFile $zipFile -UseBasicParsing
        }

        Write-Host "      Extracting repository contents..." -ForegroundColor Gray
        $extractDir = Join-Path $stagingDir 'unpacked'
        Expand-Archive -LiteralPath $zipFile -DestinationPath $extractDir -Force

        # Find the unpacked directory (typically browser-engine-main or browser-engine-master)
        $unpackedRoot = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
        if ($null -eq $unpackedRoot) {
            throw "Failed to find unpacked repository root directory in '$extractDir'."
        }
        $sourceDir = $unpackedRoot.FullName
        Write-Host "      Downloaded and unpacked successfully." -ForegroundColor Gray
    }

    # 2. Always build cleanly into an isolated staging directory (never copy repo sources into InstallPath)
    Write-Host "`n[2/4] Publishing clean single-file MishaWeb.exe to staging..." -ForegroundColor Green
    $projectFile = Join-Path $sourceDir 'desktop\MishaWeb.csproj'
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "MishaWeb.csproj not found at '$projectFile'."
    }

    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCmd) {
        throw "Microsoft .NET 10 SDK was not found on your system. Please install .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0"
    }

    $publishStagingDir = Join-Path $stagingDir 'publish'
    New-Item -ItemType Directory -Path $publishStagingDir -Force | Out-Null

    $lockFile = Join-Path $sourceDir 'desktop\packages.lock.json'
    $restoreLockedMode = if (Test-Path -LiteralPath $lockFile -PathType Leaf) { 'true' } else { 'false' }

    & dotnet publish $projectFile `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $publishStagingDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=false `
        -p:TieredPGO=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:ContinuousIntegrationBuild=true `
        "-p:RestoreLockedMode=$restoreLockedMode" `
        -p:Deterministic=true

    $stagedExe = Join-Path $publishStagingDir 'MishaWeb.exe'
    if ($LASTEXITCODE -ne 0 -or (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf))) {
        throw "Failed to compile MishaWeb.exe. dotnet publish exited with code $LASTEXITCODE."
    }
    if ((Get-Item -LiteralPath $stagedExe).Length -le 0) {
        throw "Staged MishaWeb.exe at '$stagedExe' is empty."
    }

    # 3. Atomically swap staged build into InstallPath with backup rollback on failure
    Write-Host "`n[3/4] Installing verified build into '$installFullPath'..." -ForegroundColor Green
    if (Test-Path -LiteralPath $installFullPath -PathType Leaf) {
        throw "Target path '$installFullPath' already exists as a file."
    }

    $installParent = Split-Path -Parent $installFullPath
    if ([string]::IsNullOrWhiteSpace($installParent)) {
        throw "InstallPath '$installFullPath' must have a parent directory."
    }
    if (-not (Test-Path -LiteralPath $installParent)) {
        New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    }

    $targetExe = Join-Path $installFullPath 'MishaWeb.exe'
    $targetStageDir = Join-Path $installParent (".MishaWeb_Stage_$([Guid]::NewGuid().ToString('N'))")
    $backupDir = Join-Path $installParent (".MishaWeb_Backup_$([Guid]::NewGuid().ToString('N'))")
    $movedBackup = $false
    try {
        # Materialize the verified staging tree on the target volume before swapping.
        Copy-Item -LiteralPath $publishStagingDir -Destination $targetStageDir -Recurse -Force
        if (Test-Path -LiteralPath $installFullPath) {
            Move-Item -LiteralPath $installFullPath -Destination $backupDir
            $movedBackup = $true
        }
        Move-Item -LiteralPath $targetStageDir -Destination $installFullPath
        if (-not (Test-Path -LiteralPath $targetExe -PathType Leaf)) {
            throw "Installed executable missing at '$targetExe' after commit."
        }
    }
    catch {
        if (Test-Path -LiteralPath $targetStageDir) {
            Remove-Item -LiteralPath $targetStageDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $installFullPath) {
            Remove-Item -LiteralPath $installFullPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($movedBackup -and (Test-Path -LiteralPath $backupDir)) {
            Move-Item -LiteralPath $backupDir -Destination $installFullPath -ErrorAction SilentlyContinue
        }
        throw
    }

    if ($movedBackup -and (Test-Path -LiteralPath $backupDir)) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "      MishaWeb.exe installed and verified cleanly." -ForegroundColor Green

    # 4. Create Shortcuts
    Write-Host "`n[4/4] Creating Desktop and Start Menu shortcuts..." -ForegroundColor Green
    $wshShell = New-Object -ComObject WScript.Shell

    # Desktop Shortcut
    $desktopPath = [Environment]::GetFolderPath('Desktop')
    $desktopShortcutPath = Join-Path $desktopPath 'MishaWeb.lnk'
    $desktopShortcut = $wshShell.CreateShortcut($desktopShortcutPath)
    $desktopShortcut.TargetPath = $targetExe
    $desktopShortcut.WorkingDirectory = $installFullPath
    $desktopShortcut.Description = 'MishaWeb Browser'
    $desktopShortcut.Save()
    Write-Host "      Desktop shortcut created: $desktopShortcutPath" -ForegroundColor Gray

    # Start Menu Shortcut
    $startMenuPrograms = [Environment]::GetFolderPath('Programs')
    $startMenuDir = Join-Path $startMenuPrograms 'MishaWeb'
    if (-not (Test-Path $startMenuDir)) {
        New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    }
    $startMenuShortcutPath = Join-Path $startMenuDir 'MishaWeb.lnk'
    $startMenuShortcut = $wshShell.CreateShortcut($startMenuShortcutPath)
    $startMenuShortcut.TargetPath = $targetExe
    $startMenuShortcut.WorkingDirectory = $installFullPath
    $startMenuShortcut.Description = 'MishaWeb Browser'
    $startMenuShortcut.Save()
    Write-Host "      Start Menu shortcut created: $startMenuShortcutPath" -ForegroundColor Gray

    Write-Host "`n==================================================================" -ForegroundColor Green
    Write-Host "              MISHAWEB INSTALLATION SUCCESSFUL!                   " -ForegroundColor Green
    Write-Host "==================================================================" -ForegroundColor Green

    # Display Finished Pop-up Dialog with Checkmark to Open MishaWeb.exe
    if (-not $NoGui) {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing

        [System.Windows.Forms.Application]::EnableVisualStyles()

        $form = New-Object System.Windows.Forms.Form
        $form.Text = "MishaWeb Setup"
        $form.Size = New-Object System.Drawing.Size(520, 310)
        $form.StartPosition = "CenterScreen"
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
        $form.MaximizeBox = $false
        $form.MinimizeBox = $false
        $form.TopMost = $true
        $form.BackColor = [System.Drawing.Color]::White

        # Header Panel
        $headerPanel = New-Object System.Windows.Forms.Panel
        $headerPanel.Dock = [System.Windows.Forms.DockStyle]::Top
        $headerPanel.Height = 70
        $headerPanel.BackColor = [System.Drawing.Color]::FromArgb(15, 45, 89)
        $form.Controls.Add($headerPanel)

        $titleFont = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
        $disposables.Add($titleFont)
        $titleLabel = New-Object System.Windows.Forms.Label
        $titleLabel.Text = "Installation Finished!"
        $titleLabel.Font = $titleFont
        $titleLabel.ForeColor = [System.Drawing.Color]::White
        $titleLabel.Location = New-Object System.Drawing.Point(22, 12)
        $titleLabel.AutoSize = $true
        $headerPanel.Controls.Add($titleLabel)

        $subTitleFont = New-Object System.Drawing.Font("Segoe UI", 9.5)
        $disposables.Add($subTitleFont)
        $subTitleLabel = New-Object System.Windows.Forms.Label
        $subTitleLabel.Text = "MishaWeb is now installed and ready to use."
        $subTitleLabel.Font = $subTitleFont
        $subTitleLabel.ForeColor = [System.Drawing.Color]::FromArgb(215, 230, 250)
        $subTitleLabel.Location = New-Object System.Drawing.Point(24, 42)
        $subTitleLabel.AutoSize = $true
        $headerPanel.Controls.Add($subTitleLabel)

        # Body Container
        $bodyPanel = New-Object System.Windows.Forms.Panel
        $bodyPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
        $bodyPanel.Padding = New-Object System.Windows.Forms.Padding(24)
        $form.Controls.Add($bodyPanel)

        $infoFont = New-Object System.Drawing.Font("Segoe UI", 9.5)
        $disposables.Add($infoFont)
        $infoLabel = New-Object System.Windows.Forms.Label
        $infoLabel.Text = "Installation Directory:`n$installFullPath`n`nShortcuts have been placed on your Desktop and Start Menu."
        $infoLabel.Font = $infoFont
        $infoLabel.ForeColor = [System.Drawing.Color]::FromArgb(50, 50, 50)
        $infoLabel.Location = New-Object System.Drawing.Point(24, 85)
        $infoLabel.Size = New-Object System.Drawing.Size(460, 60)
        $form.Controls.Add($infoLabel)

        # Checkbox to Open Program
        $checkboxFont = New-Object System.Drawing.Font("Segoe UI", 10.5, [System.Drawing.FontStyle]::Bold)
        $disposables.Add($checkboxFont)
        $checkbox = New-Object System.Windows.Forms.CheckBox
        $checkbox.Text = "Open MishaWeb.exe now"
        $checkbox.Font = $checkboxFont
        $checkbox.ForeColor = [System.Drawing.Color]::FromArgb(15, 45, 89)
        $checkbox.Checked = $true
        $checkbox.Location = New-Object System.Drawing.Point(26, 160)
        $checkbox.Size = New-Object System.Drawing.Size(400, 30)
        $checkbox.Cursor = [System.Windows.Forms.Cursors]::Hand
        $form.Controls.Add($checkbox)

        # Finish Button
        $buttonFont = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
        $disposables.Add($buttonFont)
        $btnFinish = New-Object System.Windows.Forms.Button
        $btnFinish.Text = "Finish"
        $btnFinish.Font = $buttonFont
        $btnFinish.Size = New-Object System.Drawing.Size(110, 38)
        $btnFinish.Location = New-Object System.Drawing.Point(370, 215)
        $btnFinish.BackColor = [System.Drawing.Color]::FromArgb(15, 45, 89)
        $btnFinish.ForeColor = [System.Drawing.Color]::White
        $btnFinish.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
        $btnFinish.FlatAppearance.BorderSize = 0
        $btnFinish.Cursor = [System.Windows.Forms.Cursors]::Hand
        $btnFinish.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $form.AcceptButton = $btnFinish
        $form.Controls.Add($btnFinish)

        # Bring form to focus
        $form.Add_Shown({
            $form.Activate()
            $btnFinish.Focus()
        })

        $result = $form.ShowDialog()

        if ($result -eq [System.Windows.Forms.DialogResult]::OK -and $checkbox.Checked) {
            Write-Host "`nLaunching MishaWeb.exe..." -ForegroundColor Cyan
            Start-Process -FilePath $targetExe -WorkingDirectory $installFullPath
        }
    }
}
finally {
    if ($null -ne $form) {
        $form.Dispose()
    }
    foreach ($disposable in $disposables) {
        try { $disposable.Dispose() } catch { }
    }
    foreach ($comObj in @($startMenuShortcut, $desktopShortcut, $wshShell)) {
        if ($null -ne $comObj -and [System.Runtime.InteropServices.Marshal]::IsComObject($comObj)) {
            try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObj) } catch { }
        }
    }
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
