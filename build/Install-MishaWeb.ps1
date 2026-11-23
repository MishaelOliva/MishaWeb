<#
.SYNOPSIS
    All-in-One Installer for MishaWeb Browser Engine.

.DESCRIPTION
    Installs and sets up MishaWeb in the user's local programs directory, preferring
    verified GitHub Release artifacts (or a local repository build) over mutable branch
    sources, creates Desktop and Start Menu shortcuts, and displays a completion dialog.
#>

[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\MishaWeb'),
    [string]$RepoUrl = 'https://github.com/MishaelOliva/browser-engine',
    [string]$Branch = '',
    [string]$ReleaseTag = 'v2.6.0',
    [string]$ExpectedSha256 = '',
    [switch]$ForceDownload,
    [switch]$NoGui
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'Continue'

# Enable TLS 1.2 / 1.3
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13

function Assert-ValidGitHubRepoUrl {
    param([Parameter(Mandatory = $true)][string]$Url)

    if ([string]::IsNullOrWhiteSpace($Url)) {
        throw 'RepoUrl cannot be empty.'
    }

    [Uri]$parsed = $null
    if (-not [Uri]::TryCreate($Url.Trim(), [UriKind]::Absolute, [ref]$parsed)) {
        throw "RepoUrl '$Url' is not a valid absolute URI."
    }

    if ($parsed.Scheme -ne 'https' -or
        -not [string]::Equals($parsed.Host, 'github.com', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::IsNullOrEmpty($parsed.UserInfo) -or
        -not [string]::IsNullOrEmpty($parsed.Query) -or
        -not [string]::IsNullOrEmpty($parsed.Fragment) -or
        -not $parsed.IsDefaultPort) {
        throw "RepoUrl '$Url' must be an HTTPS https://github.com/<owner>/<repo> URL with no credentials, query, or fragment."
    }

    $trimmedPath = $parsed.AbsolutePath.Trim('/')
    if ($trimmedPath.EndsWith('.git', [System.StringComparison]::OrdinalIgnoreCase)) {
        $trimmedPath = $trimmedPath.Substring(0, $trimmedPath.Length - 4)
    }
    if ($trimmedPath -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$') {
        throw "RepoUrl '$Url' must point to a valid GitHub <owner>/<repo> path."
    }

    $segments = $trimmedPath -split '/'
    if ($segments[0] -in @('.', '..') -or $segments[1] -in @('.', '..')) {
        throw "RepoUrl '$Url' contains invalid path segments."
    }

    return "https://github.com/$trimmedPath"
}

function Assert-ValidGitRef {
    param(
        [Parameter(Mandatory = $true)][string]$Ref,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Ref)) {
        throw "$Label cannot be empty."
    }

    $trimmed = $Ref.Trim()
    if ($trimmed.StartsWith('/') -or $trimmed.EndsWith('/') -or
        $trimmed.StartsWith('.') -or $trimmed.EndsWith('.') -or
        $trimmed.Contains('..') -or $trimmed.Contains('//') -or
        $trimmed.EndsWith('.lock', [System.StringComparison]::OrdinalIgnoreCase) -or
        $trimmed -notmatch '^[A-Za-z0-9._/-]+$') {
        throw "$Label '$Ref' is not a valid Git ref or release tag."
    }

    return $trimmed
}

function Assert-ValidSha256Hex {
    param(
        [Parameter(Mandatory = $true)][string]$Hash,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $normalized = $Hash.Trim().ToLowerInvariant()
    if ($normalized -notmatch '^[0-9a-f]{64}$') {
        throw "$Label '$Hash' is not a valid 64-character SHA-256 hex digest."
    }

    return $normalized
}

function Assert-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $prefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain below '$parentFull'; got '$candidateFull'."
    }

    return $candidateFull
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $rootItem = Get-Item -Force -LiteralPath $Path
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to mutate reparse point '$Path'."
    }

    if ($rootItem.PSIsContainer) {
        $reparseChild = Get-ChildItem -Force -Recurse -LiteralPath $Path |
            Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($null -ne $reparseChild) {
            throw "Refusing to mutate a tree containing reparse point '$($reparseChild.FullName)'."
        }
    }
}

function Remove-ContainedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $safePath = Assert-ContainedPath -Candidate $Path -Parent $Parent -Label 'Recursive delete target'
    if (Test-Path -LiteralPath $safePath) {
        Assert-NoReparseTree -Path $safePath
        Remove-Item -Force -Recurse -LiteralPath $safePath
    }
}

function Assert-X64Executable {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "'$Path' is not a PE executable."
            }

            [void]$stream.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) {
                throw "'$Path' has an invalid PE header offset."
            }

            [void]$stream.Seek($peOffset, [System.IO.SeekOrigin]::Begin)
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "'$Path' has an invalid PE signature."
            }

            if ($reader.ReadUInt16() -ne 0x8664) {
                throw "'$Path' is not an x64 executable."
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-Sha256Manifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $entries = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') {
            throw "Invalid checksum line '$line' in '$Path'."
        }
        $name = $Matches[2]
        if ($entries.ContainsKey($name)) {
            throw "Duplicate checksum entry '$name' in '$Path'."
        }
        $entries[$name] = $Matches[1].ToLowerInvariant()
    }

    if ($entries.Count -eq 0) {
        throw "Checksum manifest '$Path' is empty."
    }

    return $entries
}

function Assert-FileSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $expected = Assert-ValidSha256Hex -Hash $ExpectedHash -Label "$Label expected SHA-256"
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actual, $expected, [System.StringComparison]::Ordinal)) {
        throw "SHA-256 verification failed for $Label ('$Path'). Expected '$expected', got '$actual'."
    }
}

function Test-HttpNotFound {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    $ex = $ErrorRecord.Exception
    if ($ex -is [System.Net.WebException] -and $null -ne $ex.Response) {
        $httpResp = $ex.Response -as [System.Net.HttpWebResponse]
        if ($null -ne $httpResp -and $httpResp.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
            return $true
        }
    }
    if ($null -ne $ex.PSObject.Properties['Response'] -and $null -ne $ex.Response) {
        $statusCodeProp = $ex.Response.PSObject.Properties['StatusCode']
        if ($null -ne $statusCodeProp -and ([int]$statusCodeProp.Value -eq 404)) {
            return $true
        }
    }

    return $false
}

function Invoke-WebDownloadOptional {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile
    )

    try {
        Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
        return $true
    }
    catch {
        if (Test-HttpNotFound -ErrorRecord $_) {
            if (Test-Path -LiteralPath $OutFile) {
                Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            }
            return $false
        }
        throw
    }
}

$normalizedRepoUrl = Assert-ValidGitHubRepoUrl -Url $RepoUrl
$repoSlug = ([Uri]$normalizedRepoUrl).AbsolutePath.Trim('/')
$validatedBranch = if (-not [string]::IsNullOrWhiteSpace($Branch)) {
    Assert-ValidGitRef -Ref $Branch -Label 'Branch'
} else {
    ''
}
$validatedReleaseTag = Assert-ValidGitRef -Ref $ReleaseTag -Label 'ReleaseTag'
$validatedExpectedSha256 = if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    Assert-ValidSha256Hex -Hash $ExpectedSha256 -Label 'ExpectedSha256'
} else {
    ''
}

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "             MISHAWEB ALL-IN-ONE INSTALLER SETUP                 " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "Target Installation Directory: $InstallPath" -ForegroundColor Gray

# 1. Determine Source (Local or GitHub Release / Tag)
$scriptDir = $PSScriptRoot
$repoRootCandidate = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$isLocalRepo = (Test-Path -LiteralPath (Join-Path $repoRootCandidate 'desktop\MishaWeb.csproj') -PathType Leaf)

$installFullPath = [System.IO.Path]::GetFullPath($InstallPath).TrimEnd('\', '/')
$script:InstallDirPrefix = $installFullPath + [System.IO.Path]::DirectorySeparatorChar

$tempRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/')
$stagingDir = Join-Path $tempRoot "MishaWeb_Install_$([Guid]::NewGuid().ToString('N'))"
[void](Assert-ContainedPath -Candidate $stagingDir -Parent $tempRoot -Label 'Staging directory')
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

    $publishStagingDir = Join-Path $stagingDir 'publish'
    New-Item -ItemType Directory -Path $publishStagingDir -Force | Out-Null
    $stagedExe = Join-Path $publishStagingDir 'MishaWeb.exe'
    $stagedNotice = Join-Path $publishStagingDir 'THIRD-PARTY-NOTICES.txt'
    $sourceDir = $null
    $usedVerifiedReleaseBinary = $false

    if ($isLocalRepo -and (-not $ForceDownload)) {
        Write-Host "`n[1/4] Detected local MishaWeb repository source at:" -ForegroundColor Green
        Write-Host "      $repoRootCandidate" -ForegroundColor Gray
        $sourceDir = $repoRootCandidate
    }
    elseif ([string]::IsNullOrWhiteSpace($validatedBranch)) {
        Write-Host "`n[1/4] Downloading verified MishaWeb release '$validatedReleaseTag' from GitHub..." -ForegroundColor Green
        $releaseBaseUrl = "$normalizedRepoUrl/releases/download/$validatedReleaseTag"
        $checksumPath = Join-Path $stagingDir 'MishaWeb.sha256'
        $hasReleaseChecksum = Invoke-WebDownloadOptional -Uri "$releaseBaseUrl/MishaWeb.sha256" -OutFile $checksumPath
        $hasReleaseExe = $false

        if ($hasReleaseChecksum) {
            $checksumEntries = Read-Sha256Manifest -Path $checksumPath
            $hasReleaseExe = Invoke-WebDownloadOptional -Uri "$releaseBaseUrl/MishaWeb.exe" -OutFile $stagedExe
            if ($hasReleaseExe) {
                if (-not $checksumEntries.ContainsKey('MishaWeb.exe')) {
                    throw "Release checksum manifest '$checksumPath' does not contain an entry for 'MishaWeb.exe'."
                }
                Assert-FileSha256 -Path $stagedExe -ExpectedHash $checksumEntries['MishaWeb.exe'] -Label 'MishaWeb.exe'
                if (-not [string]::IsNullOrWhiteSpace($validatedExpectedSha256)) {
                    Assert-FileSha256 -Path $stagedExe -ExpectedHash $validatedExpectedSha256 -Label 'MishaWeb.exe (ExpectedSha256)'
                }

                if (-not (Invoke-WebDownloadOptional -Uri "$releaseBaseUrl/THIRD-PARTY-NOTICES.txt" -OutFile $stagedNotice)) {
                    $rawNoticeUrl = "https://raw.githubusercontent.com/$repoSlug/refs/tags/$validatedReleaseTag/licenses/THIRD-PARTY-NOTICES.txt"
                    Invoke-WebRequest -Uri $rawNoticeUrl -OutFile $stagedNotice -UseBasicParsing
                }
                if (-not $checksumEntries.ContainsKey('THIRD-PARTY-NOTICES.txt')) {
                    throw "Release checksum manifest '$checksumPath' does not contain an entry for 'THIRD-PARTY-NOTICES.txt'."
                }
                Assert-FileSha256 -Path $stagedNotice -ExpectedHash $checksumEntries['THIRD-PARTY-NOTICES.txt'] -Label 'THIRD-PARTY-NOTICES.txt'

                $releaseManifestPath = Join-Path $stagingDir 'MishaWeb.release.json'
                if (Invoke-WebDownloadOptional -Uri "$releaseBaseUrl/MishaWeb.release.json" -OutFile $releaseManifestPath) {
                    if (-not $checksumEntries.ContainsKey('MishaWeb.release.json')) {
                        throw "Release checksum manifest '$checksumPath' does not contain an entry for 'MishaWeb.release.json'."
                    }
                    Assert-FileSha256 -Path $releaseManifestPath -ExpectedHash $checksumEntries['MishaWeb.release.json'] -Label 'MishaWeb.release.json'
                    $releaseManifest = Get-Content -Raw -LiteralPath $releaseManifestPath | ConvertFrom-Json
                    if ($releaseManifest.product -ne 'MishaWeb' -or $releaseManifest.runtimeIdentifier -ne 'win-x64') {
                        throw "Release manifest metadata is invalid for MishaWeb win-x64."
                    }
                }

                if ((Get-Item -LiteralPath $stagedExe).Length -le 0) {
                    throw "Downloaded MishaWeb.exe at '$stagedExe' is empty."
                }
                Assert-X64Executable -Path $stagedExe
                $usedVerifiedReleaseBinary = $true
                Write-Host "      Verified release artifact SHA-256 against MishaWeb.sha256." -ForegroundColor Gray
            }
        }

        if (-not $usedVerifiedReleaseBinary) {
            $tagZipUrl = "$normalizedRepoUrl/archive/refs/tags/$validatedReleaseTag.zip"
            $zipFile = Join-Path $stagingDir 'mishaweb-source.zip'
            Write-Host "      Release binary assets unavailable; fetching tagged source archive: $tagZipUrl" -ForegroundColor Gray
            Invoke-WebRequest -Uri $tagZipUrl -OutFile $zipFile -UseBasicParsing

            if (-not [string]::IsNullOrWhiteSpace($validatedExpectedSha256)) {
                Assert-FileSha256 -Path $zipFile -ExpectedHash $validatedExpectedSha256 -Label 'Tagged source archive'
            }
            elseif ($hasReleaseChecksum -and $checksumEntries.ContainsKey('mishaweb-source.zip')) {
                Assert-FileSha256 -Path $zipFile -ExpectedHash $checksumEntries['mishaweb-source.zip'] -Label 'Tagged source archive'
            }

            Write-Host "      Extracting repository contents..." -ForegroundColor Gray
            $extractDir = Join-Path $stagingDir 'unpacked'
            Expand-Archive -LiteralPath $zipFile -DestinationPath $extractDir -Force
            Assert-NoReparseTree -Path $extractDir

            $unpackedRoot = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
            if ($null -eq $unpackedRoot) {
                throw "Failed to find unpacked repository root directory in '$extractDir'."
            }
            $sourceDir = $unpackedRoot.FullName
            Write-Host "      Downloaded and unpacked release tag '$validatedReleaseTag'." -ForegroundColor Gray
        }
    }
    else {
        Write-Host "`n[1/4] Downloading MishaWeb branch '$validatedBranch' from GitHub..." -ForegroundColor Green
        $zipUrl = "$normalizedRepoUrl/archive/refs/heads/$validatedBranch.zip"
        $zipFile = Join-Path $stagingDir 'mishaweb-source.zip'

        Write-Host "      Fetching from: $zipUrl" -ForegroundColor Gray
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipFile -UseBasicParsing

        if (-not [string]::IsNullOrWhiteSpace($validatedExpectedSha256)) {
            Assert-FileSha256 -Path $zipFile -ExpectedHash $validatedExpectedSha256 -Label 'Branch source archive'
        }

        Write-Host "      Extracting repository contents..." -ForegroundColor Gray
        $extractDir = Join-Path $stagingDir 'unpacked'
        Expand-Archive -LiteralPath $zipFile -DestinationPath $extractDir -Force
        Assert-NoReparseTree -Path $extractDir

        $unpackedRoot = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
        if ($null -eq $unpackedRoot) {
            throw "Failed to find unpacked repository root directory in '$extractDir'."
        }
        $sourceDir = $unpackedRoot.FullName
        Write-Host "      Downloaded and unpacked successfully." -ForegroundColor Gray
    }

    # 2. Build cleanly into isolated staging when installing from source, and stage THIRD-PARTY-NOTICES.txt
    if (-not $usedVerifiedReleaseBinary) {
        Write-Host "`n[2/4] Publishing clean single-file MishaWeb.exe to staging..." -ForegroundColor Green
        $projectFile = Join-Path $sourceDir 'desktop\MishaWeb.csproj'
        if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
            throw "MishaWeb.csproj not found at '$projectFile'."
        }

        $lockFile = Join-Path $sourceDir 'desktop\packages.lock.json'
        if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
            throw "Locked NuGet restore is required, but '$lockFile' is missing."
        }

        $noticeSource = Join-Path $sourceDir 'licenses\THIRD-PARTY-NOTICES.txt'
        if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) {
            throw "Required license notice file not found at '$noticeSource'."
        }

        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($null -eq $dotnetCmd) {
            throw "Microsoft .NET 10 SDK was not found on your system. Please install .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0"
        }

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
            -p:RestoreLockedMode=true `
            -p:Deterministic=true

        if ($LASTEXITCODE -ne 0 -or (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf))) {
            throw "Failed to compile MishaWeb.exe. dotnet publish exited with code $LASTEXITCODE."
        }
        if ((Get-Item -LiteralPath $stagedExe).Length -le 0) {
            throw "Staged MishaWeb.exe at '$stagedExe' is empty."
        }
        Assert-X64Executable -Path $stagedExe

        Copy-Item -LiteralPath $noticeSource -Destination $stagedNotice -Force
        if (-not (Test-Path -LiteralPath $stagedNotice -PathType Leaf)) {
            throw "Failed to stage THIRD-PARTY-NOTICES.txt at '$stagedNotice'."
        }
    }
    else {
        Write-Host "`n[2/4] Using SHA-256 verified release binary payload..." -ForegroundColor Green
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
    $installParentItem = Get-Item -Force -LiteralPath $installParent
    if (($installParentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to install under reparse-point parent directory '$installParent'."
    }

    [void](Assert-ContainedPath -Candidate $installFullPath -Parent $installParent -Label 'Install target')
    $targetExe = Join-Path $installFullPath 'MishaWeb.exe'
    $targetNotice = Join-Path $installFullPath 'THIRD-PARTY-NOTICES.txt'
    $targetStageDir = Join-Path $installParent (".MishaWeb_Stage_$([Guid]::NewGuid().ToString('N'))")
    $backupDir = Join-Path $installParent (".MishaWeb_Backup_$([Guid]::NewGuid().ToString('N'))")
    [void](Assert-ContainedPath -Candidate $targetStageDir -Parent $installParent -Label 'Install stage')
    [void](Assert-ContainedPath -Candidate $backupDir -Parent $installParent -Label 'Install backup')

    $movedBackup = $false
    try {
        Assert-NoReparseTree -Path $publishStagingDir
        # Materialize the verified staging tree on the target volume before swapping.
        Copy-Item -LiteralPath $publishStagingDir -Destination $targetStageDir -Recurse -Force
        Assert-NoReparseTree -Path $targetStageDir

        if (Test-Path -LiteralPath $installFullPath) {
            Assert-NoReparseTree -Path $installFullPath
            Move-Item -LiteralPath $installFullPath -Destination $backupDir
            $movedBackup = $true
        }
        Move-Item -LiteralPath $targetStageDir -Destination $installFullPath
        if (-not (Test-Path -LiteralPath $targetExe -PathType Leaf)) {
            throw "Installed executable missing at '$targetExe' after commit."
        }
        if (-not (Test-Path -LiteralPath $targetNotice -PathType Leaf)) {
            throw "Installed THIRD-PARTY-NOTICES.txt missing at '$targetNotice' after commit."
        }
    }
    catch {
        if (Test-Path -LiteralPath $targetStageDir) {
            try { Remove-ContainedDirectory -Path $targetStageDir -Parent $installParent } catch { }
        }
        if (Test-Path -LiteralPath $installFullPath) {
            try { Remove-ContainedDirectory -Path $installFullPath -Parent $installParent } catch { }
        }
        if ($movedBackup -and (Test-Path -LiteralPath $backupDir)) {
            Assert-NoReparseTree -Path $backupDir
            Move-Item -LiteralPath $backupDir -Destination $installFullPath -ErrorAction SilentlyContinue
        }
        throw
    }

    if ($movedBackup -and (Test-Path -LiteralPath $backupDir)) {
        try { Remove-ContainedDirectory -Path $backupDir -Parent $installParent } catch { }
    }
    Write-Host "      MishaWeb.exe and THIRD-PARTY-NOTICES.txt installed and verified cleanly." -ForegroundColor Green

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
        try { Remove-ContainedDirectory -Path $stagingDir -Parent $tempRoot } catch { }
    }
}
