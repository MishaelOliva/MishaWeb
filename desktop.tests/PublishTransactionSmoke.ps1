[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishScriptPath = Join-Path $sourceRepositoryRoot 'build\Publish-MishaWeb.ps1'
$installScriptPath = Join-Path $sourceRepositoryRoot 'build\Install-MishaWeb.ps1'
$installBatPath = Join-Path $sourceRepositoryRoot 'Install-MishaWeb.bat'
$noticeSourcePath = Join-Path $sourceRepositoryRoot 'licenses\THIRD-PARTY-NOTICES.txt'

function Parse-AstFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $tokens, [ref] $errors)
    if ($errors.Count -ne 0) {
        throw "Script '$Path' has parser errors: $($errors -join '; ')"
    }
    return $ast
}

function Import-AstFunction {
    param(
        [Parameter(Mandatory = $true)] $Ast,
        [Parameter(Mandatory = $true)][string] $FunctionName,
        [string] $AliasName = ''
    )

    $functionAst = $Ast.Find(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                [string]::Equals($node.Name, $FunctionName, [System.StringComparison]::OrdinalIgnoreCase)
        },
        $true)
    if ($null -eq $functionAst) {
        throw "Could not find function '$FunctionName'."
    }

    $targetName = if ([string]::IsNullOrWhiteSpace($AliasName)) { $FunctionName } else { $AliasName }
    $text = $functionAst.Extent.Text -replace ("(?i)^function\s+" + [regex]::Escape($FunctionName)), "function script:$targetName"
    . ([scriptblock]::Create($text))
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock] $Action,
        [Parameter(Mandatory = $true)][string] $Message,
        [string] $Pattern = ''
    )

    $threw = $false
    $errMsg = ''
    try {
        & $Action
    }
    catch {
        $threw = $true
        $errMsg = $_.Exception.Message
    }

    Assert-True -Condition $threw -Message "$Message (expected exception, none thrown)"
    if (-not [string]::IsNullOrWhiteSpace($Pattern)) {
        Assert-True -Condition ($errMsg -match $Pattern) -Message "$Message (unexpected exception '$errMsg')"
    }
}

$publishAst = Parse-AstFile -Path $publishScriptPath
$installAst = Parse-AstFile -Path $installScriptPath

foreach ($functionName in @(
    'Remove-CommittedBackupBestEffort',
    'Clear-StaleBackupDirectories',
    'Move-OneClickRelease',
    'Move-FolderRelease',
    'Get-FileRecord',
    'Get-ReleaseMetadata',
    'Add-ReleaseMetadata'
)) {
    Import-AstFunction -Ast $publishAst -FunctionName $functionName
}
Import-AstFunction -Ast $publishAst -FunctionName 'Test-ReleaseOutput' -AliasName 'Invoke-RealTestReleaseOutput'

function Assert-ExactPath {
    param(
        [Parameter(Mandatory = $true)][string] $Candidate,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $expectedFull = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\', '/')
    if (-not [string]::Equals($candidateFull, $expectedFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label mismatch."
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
        throw "$Label escaped the fixture root."
    }

    return $candidateFull
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string] $Path)
}

function Get-PayloadNames {
    param([Parameter(Mandatory = $true)][bool] $SelfContained)

    if ($script:useRealPayloadNames) {
        $names = @('MishaWeb.exe', 'THIRD-PARTY-NOTICES.txt')
        if ($SelfContained) {
            $names += 'DOTNET-LICENSE.txt'
            $names += 'DOTNET-THIRD-PARTY-NOTICES.txt'
        }
        return $names
    }

    return @('MishaWeb.exe', 'MishaWeb.dll')
}

function Get-LegacyOneClickNames {
    return @('MishaWeb.legacy')
}

function Test-ReleaseOutput {
    param(
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)] $Definition
    )

    if ($script:forceValidationFailure) {
        throw 'forced pre-commit validation failure'
    }

    return [pscustomobject]@{
        Executable = Join-Path $OutputPath 'MishaWeb.exe'
        Bytes = 1234
        Sha256 = ('0' * 64)
        AuthenticodeStatus = 'NotSigned'
    }
}

function Remove-ContainedDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Parent
    )

    [void] (Assert-ContainedPath -Candidate $Path -Parent $Parent -Label 'Recursive delete target')
    if ($script:forceCleanupFailure) {
        throw 'forced locked-backup cleanup failure'
    }

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -Force -Recurse -LiteralPath $Path
    }
}

function New-ReleaseFixture {
    param([Parameter(Mandatory = $true)][string] $Name)

    $fixtureRepository = Join-Path $script:fixtureRoot $Name
    $fixtureBuild = Join-Path $fixtureRepository 'build'
    $fixtureDesktop = Join-Path $fixtureRepository 'desktop'
    $fixtureDestination = Join-Path $fixtureDesktop 'publish'
    $fixtureStaging = Join-Path $script:fixtureRoot "$Name-staging"
    New-Item -ItemType Directory -Path $fixtureRepository, $fixtureBuild, $fixtureDesktop, $fixtureDestination, $fixtureStaging | Out-Null

    $names = @((Get-PayloadNames -SelfContained $false) + $script:manifestName + $script:checksumName)
    foreach ($name in $names) {
        Set-Content -NoNewline -LiteralPath (Join-Path $fixtureRepository $name) -Value "old:$name"
        Set-Content -NoNewline -LiteralPath (Join-Path $fixtureDestination $name) -Value "old:$name"
        Set-Content -NoNewline -LiteralPath (Join-Path $fixtureStaging $name) -Value "new:$name"
    }
    Set-Content -NoNewline -LiteralPath (Join-Path $fixtureRepository 'MishaWeb.legacy') -Value 'old:legacy'

    return [pscustomobject]@{
        Repository = $fixtureRepository
        Build = $fixtureBuild
        Desktop = $fixtureDesktop
        Destination = $fixtureDestination
        Staging = $fixtureStaging
        Names = $names
        OneClickDefinition = [pscustomobject]@{ OutputPath = $fixtureRepository }
        FolderDefinition = [pscustomobject]@{ OutputPath = $fixtureDestination }
    }
}

function Use-ReleaseFixture {
    param([Parameter(Mandatory = $true)] $Fixture)

    $script:repositoryRoot = $Fixture.Repository
    $script:buildRoot = $Fixture.Build
    $script:desktopRoot = $Fixture.Desktop
}

$script:useRealPayloadNames = $false
$script:manifestName = 'MishaWeb.release.json'
$script:checksumName = 'MishaWeb.sha256'
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:forceCleanupFailure = $false
$script:forceValidationFailure = $false
$script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'MishaWeb.PublishTransaction.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:fixtureRoot | Out-Null

try {
    # 1. Move-OneClickRelease tests
    $cleanupFixture = New-ReleaseFixture -Name 'oneclick-cleanup-failure'
    Use-ReleaseFixture -Fixture $cleanupFixture
    $script:forceCleanupFailure = $true
    $cleanupWarnings = @()
    $oneClickResult = Move-OneClickRelease `
        -StagingPath $cleanupFixture.Staging `
        -Definition $cleanupFixture.OneClickDefinition `
        -WarningVariable +cleanupWarnings

    Assert-True -Condition ($null -ne $oneClickResult -and $oneClickResult.Executable -eq (Join-Path $cleanupFixture.Repository 'MishaWeb.exe')) -Message 'Move-OneClickRelease did not return validation result.'
    foreach ($name in $cleanupFixture.Names) {
        Assert-True `
            -Condition ((Get-Content -Raw -LiteralPath (Join-Path $cleanupFixture.Repository $name)) -eq "new:$name") `
            -Message "Post-commit cleanup failure replaced '$name' with the old payload."
    }
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $cleanupFixture.Repository 'MishaWeb.legacy'))) `
        -Message 'Post-commit cleanup failure restored a legacy payload.'
    $retainedBackups = @(Get-ChildItem -Directory -LiteralPath $cleanupFixture.Build -Filter '.one-click-backup-*')
    Assert-True `
        -Condition ($retainedBackups.Count -eq 1) `
        -Message 'Locked post-commit backup was not retained for safe later cleanup.'
    Assert-True `
        -Condition (($cleanupWarnings -join "`n") -match 'installed and validated') `
        -Message 'Post-commit cleanup failure did not produce a clear warning.'

    $rollbackFixture = New-ReleaseFixture -Name 'oneclick-validation-failure'
    Use-ReleaseFixture -Fixture $rollbackFixture
    $script:forceCleanupFailure = $false
    $script:forceValidationFailure = $true
    Assert-Throws `
        -Action { [void](Move-OneClickRelease -StagingPath $rollbackFixture.Staging -Definition $rollbackFixture.OneClickDefinition) } `
        -Message 'Pre-commit validation failure did not propagate.' `
        -Pattern 'forced pre-commit validation failure'

    foreach ($name in $rollbackFixture.Names) {
        Assert-True `
            -Condition ((Get-Content -Raw -LiteralPath (Join-Path $rollbackFixture.Repository $name)) -eq "old:$name") `
            -Message "Pre-commit validation failure did not restore '$name'."
    }
    Assert-True `
        -Condition ((Get-Content -Raw -LiteralPath (Join-Path $rollbackFixture.Repository 'MishaWeb.legacy')) -eq 'old:legacy') `
        -Message 'Pre-commit validation failure did not restore the legacy payload.'
    Assert-True `
        -Condition (@(Get-ChildItem -Directory -LiteralPath $rollbackFixture.Build -Filter '.one-click-backup-*').Count -eq 0) `
        -Message 'Pre-commit rollback left a backup directory behind.'

    # 2. Move-FolderRelease tests
    $folderCleanupFixture = New-ReleaseFixture -Name 'folder-cleanup-failure'
    Use-ReleaseFixture -Fixture $folderCleanupFixture
    $script:forceValidationFailure = $false
    $script:forceCleanupFailure = $true
    $folderWarnings = @()
    $folderResult = Move-FolderRelease `
        -StagingPath $folderCleanupFixture.Staging `
        -DestinationPath $folderCleanupFixture.Destination `
        -Definition $folderCleanupFixture.FolderDefinition `
        -WarningVariable +folderWarnings

    Assert-True -Condition ($null -ne $folderResult -and $folderResult.Executable -eq (Join-Path $folderCleanupFixture.Destination 'MishaWeb.exe')) -Message 'Move-FolderRelease did not return validation result.'
    foreach ($name in $folderCleanupFixture.Names) {
        Assert-True `
            -Condition ((Get-Content -Raw -LiteralPath (Join-Path $folderCleanupFixture.Destination $name)) -eq "new:$name") `
            -Message "Folder post-commit cleanup failure replaced '$name' with old payload."
    }
    Assert-True `
        -Condition (@(Get-ChildItem -Force -Directory -LiteralPath $folderCleanupFixture.Desktop -Filter '.publish-backup-*').Count -eq 1) `
        -Message 'Folder post-commit locked backup was not retained.'
    Assert-True `
        -Condition (($folderWarnings -join "`n") -match 'installed and validated') `
        -Message 'Folder post-commit cleanup failure did not produce a clear warning.'

    $folderRollbackFixture = New-ReleaseFixture -Name 'folder-validation-failure'
    Use-ReleaseFixture -Fixture $folderRollbackFixture
    $script:forceCleanupFailure = $false
    $script:forceValidationFailure = $true
    Assert-Throws `
        -Action { [void](Move-FolderRelease -StagingPath $folderRollbackFixture.Staging -DestinationPath $folderRollbackFixture.Destination -Definition $folderRollbackFixture.FolderDefinition) } `
        -Message 'Folder pre-commit validation failure did not propagate.' `
        -Pattern 'forced pre-commit validation failure'

    foreach ($name in $folderRollbackFixture.Names) {
        Assert-True `
            -Condition ((Get-Content -Raw -LiteralPath (Join-Path $folderRollbackFixture.Destination $name)) -eq "old:$name") `
            -Message "Folder pre-commit validation failure did not restore '$name'."
    }
    Assert-True `
        -Condition (@(Get-ChildItem -Force -Directory -LiteralPath $folderRollbackFixture.Desktop -Filter '.publish-backup-*').Count -eq 0) `
        -Message 'Folder pre-commit rollback left a backup directory behind.'

    # 3. Clear-StaleBackupDirectories tests
    $staleFixture = New-ReleaseFixture -Name 'stale-sweep'
    Use-ReleaseFixture -Fixture $staleFixture
    $staleOneClick = New-Item -ItemType Directory -Path (Join-Path $staleFixture.Build '.one-click-backup-old')
    $stalePublish = New-Item -ItemType Directory -Path (Join-Path $staleFixture.Desktop '.publish-backup-old')
    $stalePortable = New-Item -ItemType Directory -Path (Join-Path $staleFixture.Desktop '.portable-backup-old')
    $keepDir = New-Item -ItemType Directory -Path (Join-Path $staleFixture.Desktop 'keep-me')
    Clear-StaleBackupDirectories
    Assert-True -Condition (-not (Test-Path -LiteralPath $staleOneClick.FullName)) -Message 'Stale .one-click-backup-* was not swept.'
    Assert-True -Condition (-not (Test-Path -LiteralPath $stalePublish.FullName)) -Message 'Stale .publish-backup-* was not swept.'
    Assert-True -Condition (-not (Test-Path -LiteralPath $stalePortable.FullName)) -Message 'Stale .portable-backup-* was not swept.'
    Assert-True -Condition (Test-Path -LiteralPath $keepDir.FullName) -Message 'Unrelated directory was removed during stale backup sweep.'

    # 4. Test-ReleaseOutput negative & positive validation checks
    $script:useRealPayloadNames = $true
    [xml] $projXml = Get-Content -Raw -LiteralPath (Join-Path $sourceRepositoryRoot 'desktop\MishaWeb.csproj')
    $script:ProductVersion = [string] (@($projXml.Project.PropertyGroup.Version)[0])
    $script:TargetFramework = [string] (@($projXml.Project.PropertyGroup.TargetFramework)[0])
    $script:RequireSignature = $false
    Import-AstFunction -Ast $publishAst -FunctionName 'Assert-X64Executable'

    $validationDir = Join-Path $script:fixtureRoot 'test-release-output'
    New-Item -ItemType Directory -Path $validationDir | Out-Null
    $fdDefinition = [pscustomobject]@{
        OutputPath = $validationDir
        SelfContained = $false
        DeploymentMode = 'framework-dependent'
        RequiresDesktopRuntime = $true
        AllowUnrelatedFiles = $false
        MaximumExecutableBytes = 16777216
    }

    $builtExeCandidate = Join-Path $sourceRepositoryRoot 'desktop\bin\Release\net10.0-windows\win-x64\MishaWeb.exe'
    $hasBuiltExe = Test-Path -LiteralPath $builtExeCandidate -PathType Leaf
    if ($hasBuiltExe) {
        Copy-Item -LiteralPath $builtExeCandidate -Destination (Join-Path $validationDir 'MishaWeb.exe') -Force
    }
    else {
        Set-Content -NoNewline -LiteralPath (Join-Path $validationDir 'MishaWeb.exe') -Value 'stub-exe'
    }
    Add-ReleaseMetadata -StagingPath $validationDir -Definition $fdDefinition -SdkVersion '10.0.103'

    if ($hasBuiltExe) {
        $verifiedOut = Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition
        Assert-True -Condition ($verifiedOut.Bytes -gt 0 -and $verifiedOut.Sha256 -match '^[0-9a-f]{64}$') -Message 'Real Test-ReleaseOutput positive check failed.'

        # Post-move fast verification (presence + byte length without full re-hash)
        $postMoveOut = Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition -StagingValidation $verifiedOut
        Assert-True -Condition ($postMoveOut.Bytes -eq $verifiedOut.Bytes -and $postMoveOut.Sha256 -eq $verifiedOut.Sha256) -Message 'Post-move fast verification failed on intact release.'

        $noticePathForSizeCheck = Join-Path $validationDir 'THIRD-PARTY-NOTICES.txt'
        $origNoticeForSizeCheck = Get-Content -Raw -LiteralPath $noticePathForSizeCheck
        Set-Content -NoNewline -LiteralPath $noticePathForSizeCheck -Value ($origNoticeForSizeCheck + 'x')
        Assert-Throws `
            -Action { [void](Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition -StagingValidation $verifiedOut) } `
            -Message 'Post-move fast verification accepted a size-drifted file.' `
            -Pattern 'Post-move size mismatch'
        Set-Content -NoNewline -LiteralPath $noticePathForSizeCheck -Value $origNoticeForSizeCheck
    }

    # Tamper sha256
    $shaFile = Join-Path $validationDir 'MishaWeb.sha256'
    $origSha = Get-Content -Raw -LiteralPath $shaFile
    Set-Content -NoNewline -LiteralPath $shaFile -Value ($origSha -replace '^[0-9a-f]{64}', ('f' * 64))
    Assert-Throws `
        -Action { [void](Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition) } `
        -Message 'Test-ReleaseOutput accepted tampered MishaWeb.sha256.' `
        -Pattern 'Checksum mismatch'
    Set-Content -NoNewline -LiteralPath $shaFile -Value $origSha

    # Extra unallowed file
    $extraFile = Join-Path $validationDir 'unexpected.txt'
    Set-Content -NoNewline -LiteralPath $extraFile -Value 'junk'
    Assert-Throws `
        -Action { [void](Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition) } `
        -Message 'Test-ReleaseOutput accepted unexpected file.' `
        -Pattern 'outside the allowlist'
    Remove-Item -Force -LiteralPath $extraFile

    # Mismatched THIRD-PARTY-NOTICES.txt
    $noticeCopy = Join-Path $validationDir 'THIRD-PARTY-NOTICES.txt'
    $origNotice = Get-Content -Raw -LiteralPath $noticeCopy
    Set-Content -NoNewline -LiteralPath $noticeCopy -Value 'tampered'
    Assert-Throws `
        -Action { [void](Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition) } `
        -Message 'Test-ReleaseOutput accepted mismatched THIRD-PARTY-NOTICES.txt.' `
        -Pattern 'THIRD-PARTY-NOTICES\.txt does not match'
    Set-Content -NoNewline -LiteralPath $noticeCopy -Value $origNotice

    # Missing manifest
    $manifestCopy = Join-Path $validationDir 'MishaWeb.release.json'
    $origManifest = Get-Content -Raw -LiteralPath $manifestCopy
    Remove-Item -Force -LiteralPath $manifestCopy
    Assert-Throws `
        -Action { [void](Invoke-RealTestReleaseOutput -OutputPath $validationDir -Definition $fdDefinition) } `
        -Message 'Test-ReleaseOutput accepted missing MishaWeb.release.json.' `
        -Pattern 'missing'
    Set-Content -NoNewline -LiteralPath $manifestCopy -Value $origManifest

    # 5. Install-MishaWeb.ps1 & Install-MishaWeb.bat syntax + supply-chain guard checks
    foreach ($fn in @(
        'Assert-ValidGitHubRepoUrl',
        'Assert-ValidGitRef',
        'Assert-ValidSha256Hex',
        'Read-Sha256Manifest',
        'Assert-FileSha256'
    )) {
        Import-AstFunction -Ast $installAst -FunctionName $fn
    }

    Assert-True `
        -Condition ((Assert-ValidGitHubRepoUrl -Url 'https://github.com/MishaelOliva/browser-engine/') -eq 'https://github.com/MishaelOliva/browser-engine') `
        -Message 'Assert-ValidGitHubRepoUrl failed on valid GitHub URL.'
    Assert-Throws -Action { [void](Assert-ValidGitHubRepoUrl -Url 'http://github.com/MishaelOliva/browser-engine') } -Message 'Accepted non-HTTPS RepoUrl.'
    Assert-Throws -Action { [void](Assert-ValidGitHubRepoUrl -Url 'https://evil.example/MishaelOliva/browser-engine') } -Message 'Accepted non-GitHub RepoUrl.'
    Assert-Throws -Action { [void](Assert-ValidGitHubRepoUrl -Url 'https://user:pass@github.com/MishaelOliva/browser-engine') } -Message 'Accepted RepoUrl with credentials.'
    Assert-Throws -Action { [void](Assert-ValidGitHubRepoUrl -Url 'https://github.com/MishaelOliva/browser-engine?foo=bar') } -Message 'Accepted RepoUrl with query.'

    Assert-True -Condition ((Assert-ValidGitRef -Ref 'v2.6.0' -Label 'ReleaseTag') -eq 'v2.6.0') -Message 'Assert-ValidGitRef rejected v2.6.0.'
    Assert-Throws -Action { [void](Assert-ValidGitRef -Ref '../evil' -Label 'Branch') } -Message 'Accepted traversal in git ref.'
    Assert-Throws -Action { [void](Assert-ValidGitRef -Ref 'main;calc' -Label 'Branch') } -Message 'Accepted shell metacharacters in git ref.'

    $installText = Get-Content -Raw -LiteralPath $installScriptPath
    Assert-True -Condition ($installText -notmatch 'RestoreLockedMode=false') -Message 'Install-MishaWeb.ps1 still allows RestoreLockedMode=false.'
    Assert-True -Condition ($installText -match 'THIRD-PARTY-NOTICES\.txt') -Message 'Install-MishaWeb.ps1 does not stage THIRD-PARTY-NOTICES.txt.'
    Assert-True -Condition ($installText -match 'Assert-NoReparseTree' -and $installText -match 'Remove-ContainedDirectory') -Message 'Install-MishaWeb.ps1 is missing reparse/contained path guards.'

    $batText = Get-Content -Raw -LiteralPath $installBatPath
    Assert-True -Condition ($batText -match 'MISHAWEB_CLI_ARGS=%\*') -Message 'Install-MishaWeb.bat does not forward %* via MISHAWEB_CLI_ARGS.'
    Assert-True -Condition ($batText -notmatch 'raw\.githubusercontent\.com/.+/(main|master)/') -Message 'Install-MishaWeb.bat still references unpinned main/master branch URLs.'

    # 6. Installer ReleaseTag & package version drift gate checks
    $sourceProjectPath = Join-Path $sourceRepositoryRoot 'desktop\MishaWeb.csproj'
    $sourcePackagePath = Join-Path $sourceRepositoryRoot 'package.json'
    $liveMeta = Get-ReleaseMetadata `
        -ProjectPath $sourceProjectPath `
        -PackagePath $sourcePackagePath `
        -InstallScriptPath $installScriptPath `
        -InstallBatPath $installBatPath
    Assert-True `
        -Condition ($liveMeta.ExpectedReleaseTag -eq "v$($liveMeta.ProductVersion)") `
        -Message 'Get-ReleaseMetadata did not return matching ExpectedReleaseTag.'

    $driftDir = Join-Path $script:fixtureRoot 'release-tag-drift'
    New-Item -ItemType Directory -Path $driftDir | Out-Null
    $driftedPs1 = Join-Path $driftDir 'Install-MishaWeb.ps1'
    $driftedBat = Join-Path $driftDir 'Install-MishaWeb.bat'
    $driftedPkg = Join-Path $driftDir 'package.json'

    Set-Content -NoNewline -LiteralPath $driftedPs1 -Value ($installText -replace "ReleaseTag\s*=\s*'[^']+'", "ReleaseTag = 'v0.0.1'")
    Assert-Throws `
        -Action {
            [void](Get-ReleaseMetadata `
                -ProjectPath $sourceProjectPath `
                -PackagePath $sourcePackagePath `
                -InstallScriptPath $driftedPs1 `
                -InstallBatPath $installBatPath)
        } `
        -Message 'Get-ReleaseMetadata accepted mismatched Install-MishaWeb.ps1 ReleaseTag.' `
        -Pattern 'Install-MishaWeb\.ps1 default ReleaseTag'

    Set-Content -NoNewline -LiteralPath $driftedBat -Value ($batText -replace 'DEFAULT_RELEASE_TAG=[^"]+', 'DEFAULT_RELEASE_TAG=v0.0.1')
    Assert-Throws `
        -Action {
            [void](Get-ReleaseMetadata `
                -ProjectPath $sourceProjectPath `
                -PackagePath $sourcePackagePath `
                -InstallScriptPath $installScriptPath `
                -InstallBatPath $driftedBat)
        } `
        -Message 'Get-ReleaseMetadata accepted mismatched Install-MishaWeb.bat DEFAULT_RELEASE_TAG.' `
        -Pattern 'Install-MishaWeb\.bat DEFAULT_RELEASE_TAG'

    $pkgText = Get-Content -Raw -LiteralPath $sourcePackagePath
    Set-Content -NoNewline -LiteralPath $driftedPkg -Value ($pkgText -replace '"version"\s*:\s*"[^"]+"', '"version": "0.0.1"')
    Assert-Throws `
        -Action {
            [void](Get-ReleaseMetadata `
                -ProjectPath $sourceProjectPath `
                -PackagePath $driftedPkg `
                -InstallScriptPath $installScriptPath `
                -InstallBatPath $installBatPath)
        } `
        -Message 'Get-ReleaseMetadata accepted mismatched package.json version.' `
        -Pattern 'package\.json version'

    Write-Host 'PASS: Publish transaction, stale backup sweep, Test-ReleaseOutput, ReleaseTag drift gate, and Install-MishaWeb guards verified.'
}
finally {
    $safeFixtureRoot = [System.IO.Path]::GetFullPath($script:fixtureRoot).TrimEnd('\', '/')
    $safeTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $tempPrefix = $safeTempRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $safeFixtureRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $safeFixtureRoot).StartsWith('MishaWeb.PublishTransaction.', [System.StringComparison]::Ordinal)) {
        throw "Refusing to remove unexpected fixture path '$safeFixtureRoot'."
    }
    if (Test-Path -LiteralPath $safeFixtureRoot) {
        Remove-Item -Force -Recurse -LiteralPath $safeFixtureRoot
    }
}
