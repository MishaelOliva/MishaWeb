[CmdletBinding()]
param(
    [ValidateSet('FrameworkDependent', 'Portable', 'OneClick')]
    [string] $Mode = 'FrameworkDependent',

    [switch] $SkipChecks,

    [switch] $ValidateOnly,

    [switch] $RequireSignature,

    [string] $SigningCertificateThumbprint,

    [string] $TimestampServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$scriptPath = [System.IO.Path]::GetFullPath($PSCommandPath)
$buildRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $buildRoot '..'))
$desktopRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'desktop'))
$projectPath = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot 'MishaWeb.csproj'))
$testProjectPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'desktop.tests\MishaWeb.SmokeTests.csproj'))
$publishTransactionTestPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'desktop.tests\PublishTransactionSmoke.ps1'))
$packagePath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'package.json'))
$noticeSourcePath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'licenses\THIRD-PARTY-NOTICES.txt'))
$dependencyInventoryPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'licenses\DEPENDENCY-INVENTORY.md'))
$manifestName = 'MishaWeb.release.json'
$checksumName = 'MishaWeb.sha256'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Assert-ExactPath {
    param(
        [Parameter(Mandatory = $true)][string] $Candidate,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $expectedFull = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\', '/')
    if (-not [string]::Equals($candidateFull, $expectedFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label resolved outside its expected path. Expected '$expectedFull'; got '$candidateFull'."
    }

    return $candidateFull
}

function Assert-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Candidate,
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $Label
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
    param([Parameter(Mandatory = $true)][string] $Path)

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
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Parent
    )

    $safePath = Assert-ContainedPath -Candidate $Path -Parent $Parent -Label 'Recursive delete target'
    if (Test-Path -LiteralPath $safePath) {
        Assert-NoReparseTree -Path $safePath
        Remove-Item -Force -Recurse -LiteralPath $safePath
    }
}

function Remove-CommittedBackupBestEffort {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $ReleaseLabel
    )

    try {
        Remove-ContainedDirectory -Path $Path -Parent $Parent
    }
    catch {
        Write-Warning (
            "$ReleaseLabel release was installed and validated, but its previous payload could not " +
            "be deleted from '$Path'. The committed release was kept. Close any process using the " +
            "old payload, then remove this backup later. Cleanup error: $($_.Exception.Message)"
        )
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-ReleaseDefinition {
    param([Parameter(Mandatory = $true)][string] $SelectedMode)

    switch ($SelectedMode) {
        'FrameworkDependent' {
            return [pscustomobject]@{
                OutputPath = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot 'publish'))
                SelfContained = $false
                DeploymentMode = 'framework-dependent'
                RequiresDesktopRuntime = $true
                AllowUnrelatedFiles = $false
                MaximumExecutableBytes = 16777216
            }
        }
        'Portable' {
            return [pscustomobject]@{
                OutputPath = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot 'portable'))
                SelfContained = $true
                DeploymentMode = 'self-contained'
                RequiresDesktopRuntime = $false
                AllowUnrelatedFiles = $false
                MaximumExecutableBytes = 268435456
            }
        }
        'OneClick' {
            return [pscustomobject]@{
                OutputPath = $repositoryRoot
                SelfContained = $false
                DeploymentMode = 'framework-dependent-one-click'
                RequiresDesktopRuntime = $true
                AllowUnrelatedFiles = $true
                MaximumExecutableBytes = 16777216
            }
        }
        default {
            throw "Unsupported release mode '$SelectedMode'."
        }
    }
}

function Get-PayloadNames {
    param([Parameter(Mandatory = $true)][bool] $SelfContained)

    $names = @('MishaWeb.exe', 'THIRD-PARTY-NOTICES.txt')
    if ($SelfContained) {
        $names += 'DOTNET-LICENSE.txt'
        $names += 'DOTNET-THIRD-PARTY-NOTICES.txt'
    }

    return $names
}

function Get-LegacyOneClickNames {
    return @(
        'MishaWeb.pdb',
        'MishaWeb.deps.json',
        'MishaWeb.runtimeconfig.json',
        'Microsoft.Web.WebView2.Core.xml',
        'Microsoft.Web.WebView2.WinForms.xml',
        'Microsoft.Web.WebView2.Wpf.xml'
    )
}

function Assert-X64Executable {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "'$Path' is not a PE executable."
            }

            [void] $stream.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) {
                throw "'$Path' has an invalid PE header offset."
            }

            [void] $stream.Seek($peOffset, [System.IO.SeekOrigin]::Begin)
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

function Get-FileRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Directory,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $path = Join-Path $Directory $Name
    $item = Get-Item -Force -LiteralPath $path
    if ($item.PSIsContainer) {
        throw "Release payload '$path' must be a file."
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    return [pscustomobject][ordered]@{
        file = $Name
        bytes = [long] $item.Length
        sha256 = $hash
    }
}

function Test-ReleaseOutput {
    param(
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)] $Definition
    )

    if (-not (Test-Path -LiteralPath $OutputPath -PathType Container)) {
        throw "Release output '$OutputPath' does not exist."
    }

    Assert-NoReparseTree -Path $OutputPath
    $payloadNames = @(Get-PayloadNames -SelfContained $Definition.SelfContained)
    $releaseNames = @($payloadNames + $manifestName + $checksumName)

    foreach ($name in $releaseNames) {
        $path = Join-Path $OutputPath $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release output is missing '$name'."
        }
    }

    $releaseNoticePath = Join-Path $OutputPath 'THIRD-PARTY-NOTICES.txt'
    $sourceNoticeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $noticeSourcePath).Hash
    $releaseNoticeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseNoticePath).Hash
    if ($releaseNoticeHash -ne $sourceNoticeHash) {
        throw 'Release THIRD-PARTY-NOTICES.txt does not match the current dependency notice source.'
    }

    if (-not $Definition.AllowUnrelatedFiles) {
        $actualNames = @(Get-ChildItem -Force -LiteralPath $OutputPath | ForEach-Object { $_.Name } | Sort-Object)
        $expectedNames = @($releaseNames | Sort-Object)
        $nameDifferences = @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames)
        if (($actualNames.Count -ne $expectedNames.Count) -or
            ($nameDifferences.Count -ne 0)) {
            throw "Release output contains files outside the allowlist. Expected: $($expectedNames -join ', '). Actual: $($actualNames -join ', ')."
        }
    }
    else {
        foreach ($legacyName in Get-LegacyOneClickNames) {
            if (Test-Path -LiteralPath (Join-Path $OutputPath $legacyName)) {
                throw "One-click output retained legacy publish file '$legacyName'."
            }
        }
    }

    $manifestPath = Join-Path $OutputPath $manifestName
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'MishaWeb') {
        throw 'Release manifest identity is invalid.'
    }
    if ($manifest.version -ne $script:ProductVersion -or $manifest.targetFramework -ne $script:TargetFramework) {
        throw 'Release manifest version or target framework does not match project metadata.'
    }
    if ($manifest.runtimeIdentifier -ne 'win-x64' -or $manifest.deploymentMode -ne $Definition.DeploymentMode) {
        throw 'Release manifest runtime or deployment mode is invalid.'
    }
    if ([bool] $manifest.selfContained -ne [bool] $Definition.SelfContained -or
        [bool] $manifest.requiresDotNetDesktopRuntime -ne [bool] $Definition.RequiresDesktopRuntime) {
        throw 'Release manifest dependency metadata is invalid.'
    }

    $manifestArtifacts = @($manifest.artifacts)
    if ($manifestArtifacts.Count -ne $payloadNames.Count) {
        throw 'Release manifest artifact count is invalid.'
    }

    foreach ($name in $payloadNames) {
        $record = @($manifestArtifacts | Where-Object { $_.file -eq $name })
        if ($record.Count -ne 1) {
            throw "Release manifest must contain exactly one record for '$name'."
        }

        $actual = Get-FileRecord -Directory $OutputPath -Name $name
        if ([long] $record[0].bytes -ne $actual.bytes -or $record[0].sha256 -ne $actual.sha256) {
            throw "Release manifest hash or size is invalid for '$name'."
        }
    }

    $checksumEntries = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $OutputPath $checksumName)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') {
            throw "Invalid checksum line '$line'."
        }
        $name = $Matches[2]
        if ($checksumEntries.ContainsKey($name)) {
            throw "Duplicate checksum entry '$name'."
        }
        $checksumEntries[$name] = $Matches[1].ToLowerInvariant()
    }

    $hashedNames = @($payloadNames + $manifestName)
    if ($checksumEntries.Count -ne $hashedNames.Count) {
        throw 'Checksum file entry count is invalid.'
    }
    foreach ($name in $hashedNames) {
        if (-not $checksumEntries.ContainsKey($name)) {
            throw "Checksum file is missing '$name'."
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $OutputPath $name)).Hash.ToLowerInvariant()
        if ($checksumEntries[$name] -ne $actualHash) {
            throw "Checksum mismatch for '$name'."
        }
    }

    $exePath = Join-Path $OutputPath 'MishaWeb.exe'
    $exeLength = (Get-Item -LiteralPath $exePath).Length
    if ($exeLength -gt $Definition.MaximumExecutableBytes) {
        throw "Executable size $exeLength exceeds the $($Definition.MaximumExecutableBytes)-byte budget for $($Definition.DeploymentMode)."
    }
    Assert-X64Executable -Path $exePath
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    if ($versionInfo.ProductVersion -ne $script:ProductVersion -or $versionInfo.FileVersion -ne "$($script:ProductVersion).0") {
        throw 'Executable product/file version does not match project metadata.'
    }

    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $exePath).Status.ToString()
    if ($manifest.authenticodeStatus -ne $signatureStatus) {
        throw 'Executable Authenticode status does not match the release manifest.'
    }
    if ($RequireSignature -and $signatureStatus -ne 'Valid') {
        throw "A valid Authenticode signature is required; current status is '$signatureStatus'."
    }

    return [pscustomobject]@{
        Executable = $exePath
        Bytes = (Get-Item -LiteralPath $exePath).Length
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash.ToLowerInvariant()
        AuthenticodeStatus = $signatureStatus
    }
}

function Add-ReleaseMetadata {
    param(
        [Parameter(Mandatory = $true)][string] $StagingPath,
        [Parameter(Mandatory = $true)] $Definition,
        [Parameter(Mandatory = $true)][string] $SdkVersion
    )

    Copy-Item -LiteralPath $noticeSourcePath -Destination (Join-Path $StagingPath 'THIRD-PARTY-NOTICES.txt')

    if ($Definition.SelfContained) {
        $dotnetCommand = Get-Command dotnet -ErrorAction Stop
        $dotnetRoot = Split-Path -Parent $dotnetCommand.Source
        $dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
        $dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
        if (-not (Test-Path -LiteralPath $dotnetLicense -PathType Leaf) -or
            -not (Test-Path -LiteralPath $dotnetNotices -PathType Leaf)) {
            throw "The .NET SDK at '$dotnetRoot' does not contain its redistribution notices."
        }
        Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $StagingPath 'DOTNET-LICENSE.txt')
        Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $StagingPath 'DOTNET-THIRD-PARTY-NOTICES.txt')
    }

    $payloadNames = @(Get-PayloadNames -SelfContained $Definition.SelfContained)
    $artifactRecords = @($payloadNames | ForEach-Object { Get-FileRecord -Directory $StagingPath -Name $_ })
    $exePath = Join-Path $StagingPath 'MishaWeb.exe'
    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $exePath).Status.ToString()

    $sourceRevision = $null
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
        $sourceRevision = $env:GITHUB_SHA
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'MishaWeb'
        version = $script:ProductVersion
        targetFramework = $script:TargetFramework
        runtimeIdentifier = 'win-x64'
        deploymentMode = $Definition.DeploymentMode
        selfContained = [bool] $Definition.SelfContained
        requiresDotNetDesktopRuntime = [bool] $Definition.RequiresDesktopRuntime
        requiresWebView2EvergreenRuntime = $true
        dotnetSdk = $SdkVersion
        sourceRevision = $sourceRevision
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        authenticodeStatus = $signatureStatus
        artifacts = $artifactRecords
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $StagingPath $manifestName), $manifestJson + "`n", $utf8NoBom)

    $checksumNames = @($payloadNames + $manifestName)
    $checksumLines = @($checksumNames | ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $StagingPath $_)).Hash.ToLowerInvariant()
        "$hash *$_"
    })
    [System.IO.File]::WriteAllText((Join-Path $StagingPath $checksumName), ($checksumLines -join "`n") + "`n", $utf8NoBom)
}

function Sign-ReleaseExecutable {
    param(
        [Parameter(Mandatory = $true)][string] $ExecutablePath,
        [Parameter(Mandatory = $true)][string] $Thumbprint,
        [Parameter(Mandatory = $true)][string] $TimestampUrl
    )

    $normalizedThumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalizedThumbprint.Length -lt 40) {
        throw 'The code-signing certificate thumbprint is invalid.'
    }
    if (-not [Uri]::IsWellFormedUriString($TimestampUrl, [UriKind]::Absolute)) {
        throw 'TimestampServer must be an absolute timestamp-service URL.'
    }

    $certificatePath = "Cert:\CurrentUser\My\$normalizedThumbprint"
    $certificate = Get-Item -LiteralPath $certificatePath -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) {
        throw "Certificate '$normalizedThumbprint' does not have an accessible private key."
    }
    $codeSigningEku = @($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' })
    if ($codeSigningEku.Count -eq 0) {
        throw "Certificate '$normalizedThumbprint' is not valid for code signing."
    }

    $result = Set-AuthenticodeSignature `
        -LiteralPath $ExecutablePath `
        -Certificate $certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampUrl
    if ($result.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed with status '$($result.Status)': $($result.StatusMessage)"
    }
}

function Move-FolderRelease {
    param(
        [Parameter(Mandatory = $true)][string] $StagingPath,
        [Parameter(Mandatory = $true)][string] $DestinationPath,
        [Parameter(Mandatory = $true)] $Definition
    )

    $destinationLeaf = Split-Path -Leaf $DestinationPath
    $expectedDestination = Join-Path $desktopRoot $destinationLeaf
    [void] (Assert-ExactPath -Candidate $DestinationPath -Expected $expectedDestination -Label 'Release destination')
    $backupPath = Join-Path $desktopRoot ".$destinationLeaf-backup-$([Guid]::NewGuid().ToString('N'))"
    [void] (Assert-ContainedPath -Candidate $backupPath -Parent $desktopRoot -Label 'Release backup')

    $oldMoved = $false
    $newMoved = $false
    try {
        if (Test-Path -LiteralPath $DestinationPath) {
            Assert-NoReparseTree -Path $DestinationPath
            Move-Item -LiteralPath $DestinationPath -Destination $backupPath
            $oldMoved = $true
        }

        Move-Item -LiteralPath $StagingPath -Destination $DestinationPath
        $newMoved = $true
        [void] (Test-ReleaseOutput -OutputPath $DestinationPath -Definition $Definition)
    }
    catch {
        if ($newMoved -and (Test-Path -LiteralPath $DestinationPath)) {
            Assert-NoReparseTree -Path $DestinationPath
            Remove-Item -Force -Recurse -LiteralPath $DestinationPath
        }
        if ($oldMoved -and (Test-Path -LiteralPath $backupPath)) {
            Move-Item -LiteralPath $backupPath -Destination $DestinationPath
        }
        throw
    }

    # Validation is the commit boundary. Backup cleanup must never roll back an
    # already-validated release merely because an old executable is still locked.
    if ($oldMoved) {
        Remove-CommittedBackupBestEffort `
            -Path $backupPath `
            -Parent $desktopRoot `
            -ReleaseLabel 'Folder'
    }
}

function Move-OneClickRelease {
    param(
        [Parameter(Mandatory = $true)][string] $StagingPath,
        [Parameter(Mandatory = $true)] $Definition
    )

    [void] (Assert-ExactPath -Candidate $Definition.OutputPath -Expected $repositoryRoot -Label 'One-click destination')
    $releaseNames = @((Get-PayloadNames -SelfContained $false) + $manifestName + $checksumName)
    $backupPath = Join-Path $buildRoot ".one-click-backup-$([Guid]::NewGuid().ToString('N'))"
    [void] (Assert-ContainedPath -Candidate $backupPath -Parent $buildRoot -Label 'One-click backup')
    New-Item -ItemType Directory -Path $backupPath | Out-Null

    $incomingPaths = @{}
    $installedNames = @()
    $movedOldNames = @()
    try {
        foreach ($name in $releaseNames) {
            $incomingPath = Join-Path $repositoryRoot ".$name.incoming-$([Guid]::NewGuid().ToString('N'))"
            [void] (Assert-ContainedPath -Candidate $incomingPath -Parent $repositoryRoot -Label 'One-click incoming file')
            Copy-Item -LiteralPath (Join-Path $StagingPath $name) -Destination $incomingPath
            $incomingPaths[$name] = $incomingPath
        }

        $oldNames = @($releaseNames + (Get-LegacyOneClickNames))
        foreach ($name in $oldNames) {
            $oldPath = Join-Path $repositoryRoot $name
            if (Test-Path -LiteralPath $oldPath) {
                Assert-NoReparseTree -Path $oldPath
                Move-Item -LiteralPath $oldPath -Destination (Join-Path $backupPath $name)
                $movedOldNames += $name
            }
        }

        foreach ($name in $releaseNames) {
            Move-Item -LiteralPath $incomingPaths[$name] -Destination (Join-Path $repositoryRoot $name)
            $installedNames += $name
        }

        [void] (Test-ReleaseOutput -OutputPath $repositoryRoot -Definition $Definition)
    }
    catch {
        foreach ($name in $installedNames) {
            $installedPath = Join-Path $repositoryRoot $name
            if (Test-Path -LiteralPath $installedPath -PathType Leaf) {
                Remove-Item -Force -LiteralPath $installedPath
            }
        }
        foreach ($incomingPath in $incomingPaths.Values) {
            if (Test-Path -LiteralPath $incomingPath -PathType Leaf) {
                Remove-Item -Force -LiteralPath $incomingPath
            }
        }
        foreach ($name in $movedOldNames) {
            $backupFile = Join-Path $backupPath $name
            if (Test-Path -LiteralPath $backupFile -PathType Leaf) {
                Move-Item -LiteralPath $backupFile -Destination (Join-Path $repositoryRoot $name)
            }
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-ContainedDirectory -Path $backupPath -Parent $buildRoot
        }
        throw
    }

    # Validation is the commit boundary. A process may still hold the old EXE
    # after it has been moved into the backup directory, so cleanup is best effort.
    Remove-CommittedBackupBestEffort `
        -Path $backupPath `
        -Parent $buildRoot `
        -ReleaseLabel 'One-click'
}

[void] (Assert-ExactPath -Candidate $scriptPath -Expected (Join-Path $buildRoot 'Publish-MishaWeb.ps1') -Label 'Release script')
[void] (Assert-ExactPath -Candidate $projectPath -Expected (Join-Path $repositoryRoot 'desktop\MishaWeb.csproj') -Label 'Desktop project')
[void] (Assert-ExactPath -Candidate $testProjectPath -Expected (Join-Path $repositoryRoot 'desktop.tests\MishaWeb.SmokeTests.csproj') -Label 'Test project')
foreach ($requiredPath in @(
    $projectPath,
    $testProjectPath,
    $publishTransactionTestPath,
    $packagePath,
    $noticeSourcePath,
    $dependencyInventoryPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release input '$requiredPath' does not exist."
    }
}

[xml] $projectXml = Get-Content -Raw -LiteralPath $projectPath
$script:ProductVersion = [string] (@($projectXml.Project.PropertyGroup.Version)[0])
$script:TargetFramework = [string] (@($projectXml.Project.PropertyGroup.TargetFramework)[0])
$packageMetadata = Get-Content -Raw -LiteralPath $packagePath | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($script:ProductVersion) -or [string]::IsNullOrWhiteSpace($script:TargetFramework)) {
    throw 'Project version or target framework metadata is missing.'
}
if ($packageMetadata.version -ne $script:ProductVersion) {
    throw "package.json version '$($packageMetadata.version)' does not match project version '$($script:ProductVersion)'."
}
if ($script:TargetFramework -ne 'net10.0-windows') {
    throw "Release project must target net10.0-windows; found '$($script:TargetFramework)'."
}

$webView2References = @($projectXml.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Microsoft.Web.WebView2' })
if ($webView2References.Count -ne 1) {
    throw 'The release project must contain exactly one Microsoft.Web.WebView2 package reference.'
}
$webView2VersionSpec = [string] $webView2References[0].Version
$exactVersionMatch = [regex]::Match(
    $webView2VersionSpec,
    '^\[(?<version>[0-9]+(?:\.[0-9]+){2,3})\]$')
if (-not $exactVersionMatch.Success) {
    throw "Microsoft.Web.WebView2 must use an exact bracketed version; found '$webView2VersionSpec'."
}
$webView2Version = $exactVersionMatch.Groups['version'].Value
$noticeText = Get-Content -Raw -LiteralPath $noticeSourcePath
$noticeVersionMatches = [regex]::Matches(
    $noticeText,
    '(?m)^Microsoft\.Web\.WebView2 (?<version>[0-9.]+)\r?$')
$noticeUrlMatches = [regex]::Matches(
    $noticeText,
    '(?m)^Package: https://www\.nuget\.org/packages/Microsoft\.Web\.WebView2/(?<version>[0-9.]+)\r?$')
$inventoryText = Get-Content -Raw -LiteralPath $dependencyInventoryPath
$inventoryVersionMatches = [regex]::Matches(
    $inventoryText,
    '(?m)^\| Microsoft\.Web\.WebView2 (?<version>[0-9.]+) managed controls and x64 loader \|')
foreach ($entry in @(
    [pscustomobject]@{ Label = 'third-party notice version'; Matches = $noticeVersionMatches },
    [pscustomobject]@{ Label = 'third-party notice package URL'; Matches = $noticeUrlMatches },
    [pscustomobject]@{ Label = 'dependency inventory version'; Matches = $inventoryVersionMatches }
)) {
    if ($entry.Matches.Count -ne 1 -or $entry.Matches[0].Groups['version'].Value -ne $webView2Version) {
        throw "The $($entry.Label) must appear exactly once and match Microsoft.Web.WebView2 $webView2Version."
    }
}

$definition = Get-ReleaseDefinition -SelectedMode $Mode
if ($Mode -eq 'OneClick') {
    [void] (Assert-ExactPath -Candidate $definition.OutputPath -Expected $repositoryRoot -Label 'Release output')
}
else {
    [void] (Assert-ContainedPath -Candidate $definition.OutputPath -Parent $desktopRoot -Label 'Release output')
}

if ($ValidateOnly) {
    if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint) -or
        -not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        throw 'Signing options cannot be used with ValidateOnly.'
    }
    $validation = Test-ReleaseOutput -OutputPath $definition.OutputPath -Definition $definition
    Write-Host "Validated $Mode release: $($validation.Executable)"
    Write-Host "SHA-256: $($validation.Sha256)"
    Write-Host "Authenticode: $($validation.AuthenticodeStatus)"
    exit 0
}

$dotnetSdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetSdkVersion)) {
    throw 'Unable to resolve the .NET SDK selected by global.json.'
}

$mutexNameBytes = [System.Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToLowerInvariant())
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $mutexSuffix = ([System.BitConverter]::ToString($sha.ComputeHash($mutexNameBytes))).Replace('-', '').Substring(0, 24)
}
finally {
    $sha.Dispose()
}
$releaseMutex = New-Object System.Threading.Mutex($false, "Local\MishaWeb.Release.$mutexSuffix")
$ownsMutex = $false
$stagingRoot = Join-Path $desktopRoot '.release-staging'
$stagingPath = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))

try {
    $ownsMutex = $releaseMutex.WaitOne(0)
    if (-not $ownsMutex) {
        throw 'Another release operation is already running for this workspace.'
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        $stagingRootItem = Get-Item -Force -LiteralPath $stagingRoot
        if (($stagingRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to use reparse-point staging root '$stagingRoot'."
        }
    }
    else {
        New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    }
    [void] (Assert-ContainedPath -Candidate $stagingPath -Parent $stagingRoot -Label 'Unique staging directory')
    New-Item -ItemType Directory -Path $stagingPath | Out-Null

    $selfContainedText = $definition.SelfContained.ToString().ToLowerInvariant()

    if (-not $SkipChecks) {
        Invoke-DotNet -Arguments @('restore', $testProjectPath, '--locked-mode')
        Invoke-DotNet -Arguments @(
            'build', $testProjectPath,
            '--configuration', 'Release',
            '--no-restore',
            '--warnaserror',
            '-p:ContinuousIntegrationBuild=true'
        )
        Invoke-DotNet -Arguments @(
            'run',
            '--project', $testProjectPath,
            '--configuration', 'Release',
            '--no-build',
            '--no-restore'
        )
        & powershell -NoProfile -ExecutionPolicy Bypass -File $publishTransactionTestPath
        if ($LASTEXITCODE -ne 0) {
            throw "Publish transaction smoke tests failed with exit code $LASTEXITCODE."
        }
    }

    Invoke-DotNet -Arguments @(
        'restore', $projectPath,
        '--runtime', 'win-x64',
        '--locked-mode',
        "-p:SelfContained=$selfContainedText",
        "-p:PublishSelfContained=$selfContainedText"
    )

    Invoke-DotNet -Arguments @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--no-restore',
        '--warnaserror',
        '--self-contained', $selfContainedText,
        '--output', $stagingPath,
        '-p:PublishSingleFile=true',
        "-p:PublishSelfContained=$selfContainedText",
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishReadyToRun=false',
        '-p:TieredPGO=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true'
    )

    $rawPublishItems = @(Get-ChildItem -Force -LiteralPath $stagingPath)
    if ($rawPublishItems.Count -ne 1 -or $rawPublishItems[0].PSIsContainer -or $rawPublishItems[0].Name -ne 'MishaWeb.exe') {
        $rawNames = @($rawPublishItems | ForEach-Object { $_.Name })
        throw "Single-file publish produced an unexpected payload: $($rawNames -join ', ')."
    }

    Assert-X64Executable -Path (Join-Path $stagingPath 'MishaWeb.exe')
    $publishedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stagingPath 'MishaWeb.exe'))
    if ($publishedVersion.ProductVersion -ne $script:ProductVersion -or $publishedVersion.FileVersion -ne "$($script:ProductVersion).0") {
        throw 'Staged executable version does not match source metadata.'
    }

    if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        if ([string]::IsNullOrWhiteSpace($TimestampServer)) {
            throw 'TimestampServer is required when SigningCertificateThumbprint is provided.'
        }
        Sign-ReleaseExecutable `
            -ExecutablePath (Join-Path $stagingPath 'MishaWeb.exe') `
            -Thumbprint $SigningCertificateThumbprint `
            -TimestampUrl $TimestampServer
    }
    elseif (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        throw 'SigningCertificateThumbprint is required when TimestampServer is provided.'
    }

    Add-ReleaseMetadata -StagingPath $stagingPath -Definition $definition -SdkVersion $dotnetSdkVersion
    $stagedValidation = Test-ReleaseOutput -OutputPath $stagingPath -Definition $definition

    if ($Mode -eq 'OneClick') {
        Move-OneClickRelease -StagingPath $stagingPath -Definition $definition
    }
    else {
        Move-FolderRelease -StagingPath $stagingPath -DestinationPath $definition.OutputPath -Definition $definition
    }

    $finalValidation = Test-ReleaseOutput -OutputPath $definition.OutputPath -Definition $definition
    Write-Host "Published $Mode release: $($finalValidation.Executable)"
    Write-Host "Bytes: $($finalValidation.Bytes)"
    Write-Host "SHA-256: $($finalValidation.Sha256)"
    Write-Host "Authenticode: $($finalValidation.AuthenticodeStatus)"
    if ($finalValidation.AuthenticodeStatus -ne 'Valid') {
        Write-Warning 'This artifact is not Authenticode-signed. Sign production artifacts with a trusted code-signing certificate, then regenerate the manifest/checksums before distribution.'
    }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-ContainedDirectory -Path $stagingPath -Parent $stagingRoot
    }
    if (Test-Path -LiteralPath $stagingRoot) {
        $remaining = @(Get-ChildItem -Force -LiteralPath $stagingRoot)
        if ($remaining.Count -eq 0) {
            Remove-Item -Force -LiteralPath $stagingRoot
        }
    }
    if ($ownsMutex) {
        $releaseMutex.ReleaseMutex()
    }
    $releaseMutex.Dispose()
}
