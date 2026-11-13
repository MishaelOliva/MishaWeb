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

$stagingDir = Join-Path $env:TEMP "MishaWeb_Install_$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
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

    # 2. Prepare Installation Directory
    Write-Host "`n[2/4] Setting up installation folder..." -ForegroundColor Green
    if (-not (Test-Path $InstallPath)) {
        New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    }

    # Copy full repository folder / source files to installation directory
    Write-Host "      Copying MishaWeb application and engine assets..." -ForegroundColor Gray
    Copy-Item -Path "$sourceDir\*" -Destination $InstallPath -Recurse -Force

    # 3. Ensure MishaWeb.exe is Ready
    Write-Host "`n[3/4] Validating MishaWeb.exe binary..." -ForegroundColor Green
    $targetExe = Join-Path $InstallPath 'MishaWeb.exe'

    $needsBuild = $true
    if (Test-Path -LiteralPath $targetExe) {
        $exeSize = (Get-Item -LiteralPath $targetExe).Length
        if ($exeSize -gt 1000000) { # Valid compiled single-file binary > 1MB
            Write-Host "      Found pre-compiled MishaWeb.exe ($([math]::Round($exeSize / 1MB, 2)) MB)." -ForegroundColor Gray
            $needsBuild = $false
        }
    }

    if ($needsBuild) {
        Write-Host "      Building optimized single-file MishaWeb.exe with .NET 10 LTS..." -ForegroundColor Yellow
        $projectFile = Join-Path $InstallPath 'desktop\MishaWeb.csproj'
        if (-not (Test-Path -LiteralPath $projectFile)) {
            throw "MishaWeb.csproj not found at '$projectFile'."
        }

        # Check for dotnet SDK
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($null -eq $dotnetCmd) {
            throw "Microsoft .NET 10 SDK was not found on your system. Please install .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0"
        }

        & dotnet publish $projectFile `
            --configuration Release `
            --runtime win-x64 `
            --self-contained false `
            --output $InstallPath `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:PublishReadyToRun=false `
            -p:PublishTrimmed=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:ContinuousIntegrationBuild=true `
            -p:RestoreLockedMode=false `
            -p:Deterministic=true

        if ($LASTEXITCODE -ne 0 -or (-not (Test-Path -LiteralPath $targetExe))) {
            throw "Failed to compile MishaWeb.exe. dotnet publish exited with code $LASTEXITCODE."
        }
        Write-Host "      MishaWeb.exe successfully compiled and placed in installation folder." -ForegroundColor Green
    }

    # Create Shortcuts
    Write-Host "`n[4/4] Creating Desktop and Start Menu shortcuts..." -ForegroundColor Green
    $wshShell = New-Object -ComObject WScript.Shell

    # Desktop Shortcut
    $desktopPath = [Environment]::GetFolderPath('Desktop')
    $desktopShortcutPath = Join-Path $desktopPath 'MishaWeb.lnk'
    $shortcut = $wshShell.CreateShortcut($desktopShortcutPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = 'MishaWeb High-Performance Browser Engine'
    $shortcut.Save()
    Write-Host "      Desktop shortcut created: $desktopShortcutPath" -ForegroundColor Gray

    # Start Menu Shortcut
    $startMenuPrograms = [Environment]::GetFolderPath('Programs')
    $startMenuDir = Join-Path $startMenuPrograms 'MishaWeb'
    if (-not (Test-Path $startMenuDir)) {
        New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    }
    $startMenuShortcutPath = Join-Path $startMenuDir 'MishaWeb.lnk'
    $smShortcut = $wshShell.CreateShortcut($startMenuShortcutPath)
    $smShortcut.TargetPath = $targetExe
    $smShortcut.WorkingDirectory = $InstallPath
    $smShortcut.Description = 'MishaWeb High-Performance Browser Engine'
    $smShortcut.Save()
    Write-Host "      Start Menu shortcut created: $startMenuShortcutPath" -ForegroundColor Gray

    Write-Host "`n==================================================================" -ForegroundColor Green
    Write-Host "              MISHAWEB INSTALLATION SUCCESSFUL!                   " -ForegroundColor Green
    Write-Host "==================================================================" -ForegroundColor Green

    # 4. Display Finished Pop-up Dialog with Checkmark to Open MishaWeb.exe
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
        $headerPanel.BackColor = [System.Drawing.Color]::FromArgb(15, 45, 89) # Executive MishaWeb Navy
        $form.Controls.Add($headerPanel)

        $titleLabel = New-Object System.Windows.Forms.Label
        $titleLabel.Text = "Installation Finished!"
        $titleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
        $titleLabel.ForeColor = [System.Drawing.Color]::White
        $titleLabel.Location = New-Object System.Drawing.Point(22, 12)
        $titleLabel.AutoSize = $true
        $headerPanel.Controls.Add($titleLabel)

        $subTitleLabel = New-Object System.Windows.Forms.Label
        $subTitleLabel.Text = "MishaWeb Browser Engine is now installed and ready to use."
        $subTitleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
        $subTitleLabel.ForeColor = [System.Drawing.Color]::FromArgb(215, 230, 250)
        $subTitleLabel.Location = New-Object System.Drawing.Point(24, 42)
        $subTitleLabel.AutoSize = $true
        $headerPanel.Controls.Add($subTitleLabel)

        # Body Container
        $bodyPanel = New-Object System.Windows.Forms.Panel
        $bodyPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
        $bodyPanel.Padding = New-Object System.Windows.Forms.Padding(24)
        $form.Controls.Add($bodyPanel)

        $infoLabel = New-Object System.Windows.Forms.Label
        $infoLabel.Text = "Installation Directory:`n$InstallPath`n`nShortcuts have been placed on your Desktop and Start Menu."
        $infoLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
        $infoLabel.ForeColor = [System.Drawing.Color]::FromArgb(50, 50, 50)
        $infoLabel.Location = New-Object System.Drawing.Point(24, 85)
        $infoLabel.Size = New-Object System.Drawing.Size(460, 60)
        $form.Controls.Add($infoLabel)

        # Checkbox to Open Program
        $checkbox = New-Object System.Windows.Forms.CheckBox
        $checkbox.Text = "Open MishaWeb.exe program now"
        $checkbox.Font = New-Object System.Drawing.Font("Segoe UI", 10.5, [System.Drawing.FontStyle]::Bold)
        $checkbox.ForeColor = [System.Drawing.Color]::FromArgb(15, 45, 89)
        $checkbox.Checked = $true
        $checkbox.Location = New-Object System.Drawing.Point(26, 160)
        $checkbox.Size = New-Object System.Drawing.Size(400, 30)
        $checkbox.Cursor = [System.Windows.Forms.Cursors]::Hand
        $form.Controls.Add($checkbox)

        # Finish Button
        $btnFinish = New-Object System.Windows.Forms.Button
        $btnFinish.Text = "Finish"
        $btnFinish.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
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

        if ($checkbox.Checked) {
            Write-Host "`nLaunching MishaWeb.exe..." -ForegroundColor Cyan
            Start-Process -FilePath $targetExe -WorkingDirectory $InstallPath
        }
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
