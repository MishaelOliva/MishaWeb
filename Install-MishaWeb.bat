@echo off
setlocal EnableDelayedExpansion
title MishaWeb All-in-One Installer

echo ==================================================================
echo              MISHAWEB ALL-IN-ONE ONE-CLICK INSTALLER              
echo ==================================================================
echo.

set "SCRIPT_DIR=%~dp0"
set "LOCAL_SCRIPT=%SCRIPT_DIR%build\Install-MishaWeb.ps1"
set "DEFAULT_RELEASE_TAG=v2.6.0"

if exist "%LOCAL_SCRIPT%" (
    echo [INFO] Running installer from local repository package...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LOCAL_SCRIPT%" %*
) else (
    echo [INFO] Running standalone installer...
    echo [INFO] Downloading release-pinned installation script from GitHub ^(%DEFAULT_RELEASE_TAG%^)...
    set "MISHAWEB_CLI_ARGS=%*"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
        "$ErrorActionPreference = 'Stop';" ^
        "$rawArgs = $env:MISHAWEB_CLI_ARGS;" ^
        "$forwardedNamed = @{};" ^
        "$forwardedPositional = New-Object System.Collections.Generic.List[object];" ^
        "$releaseTag = '%DEFAULT_RELEASE_TAG%';" ^
        "$expectedScriptSha256 = $env:MISHAWEB_INSTALLER_SHA256;" ^
        "if (-not [string]::IsNullOrWhiteSpace($rawArgs)) {" ^
        "    $parseErrs = $null;" ^
        "    $tokens = [System.Management.Automation.PSParser]::Tokenize(('noop ' + $rawArgs), [ref]$parseErrs);" ^
        "    if ($parseErrs -and $parseErrs.Count -gt 0) { throw ('Failed to parse CLI arguments: ' + ($parseErrs[0].Message)); }" ^
        "    for ($i = 1; $i -lt $tokens.Count; $i++) {" ^
        "        $tok = $tokens[$i];" ^
        "        if ($tok.Type -eq 'CommandParameter') {" ^
        "            $pName = $tok.Content.TrimStart('-').TrimEnd(':');" ^
        "            $hasVal = ($i + 1 -lt $tokens.Count) -and ($tokens[$i + 1].Type -in @('CommandArgument', 'String', 'Number'));" ^
        "            if ($hasVal) {" ^
        "                $pVal = $tokens[$i + 1].Content;" ^
        "                $i++;" ^
        "                if ($pName -ieq 'ReleaseTag') { $releaseTag = $pVal; }" ^
        "                if ($pName -ieq 'ExpectedScriptSha256') { $expectedScriptSha256 = $pVal; continue; }" ^
        "                $forwardedNamed[$pName] = $pVal;" ^
        "            } else {" ^
        "                $forwardedNamed[$pName] = $true;" ^
        "            }" ^
        "        } elseif ($tok.Type -in @('CommandArgument', 'String', 'Number')) {" ^
        "            $forwardedPositional.Add($tok.Content);" ^
        "        }" ^
        "    }" ^
        "}" ^
        "if ($releaseTag -notmatch '^[A-Za-z0-9._-]+$' -or $releaseTag.Contains('..')) { throw \"Invalid ReleaseTag '$releaseTag'.\"; }" ^
        "$tempDir = Join-Path $env:TEMP ('MishaWeb_Bootstrap_' + [System.Guid]::NewGuid().ToString('N'));" ^
        "New-Item -ItemType Directory -Path $tempDir -Force | Out-Null;" ^
        "$scriptPath = Join-Path $tempDir 'Install-MishaWeb.ps1';" ^
        "$shaPath = Join-Path $tempDir 'Install-MishaWeb.ps1.sha256';" ^
        "try {" ^
        "    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13;" ^
        "    $releaseAssetUrl = \"https://github.com/MishaelOliva/browser-engine/releases/download/$releaseTag/Install-MishaWeb.ps1\";" ^
        "    $taggedRawUrl = \"https://raw.githubusercontent.com/MishaelOliva/browser-engine/refs/tags/$releaseTag/build/Install-MishaWeb.ps1\";" ^
        "    try {" ^
        "        Invoke-WebRequest -Uri $releaseAssetUrl -OutFile $scriptPath -UseBasicParsing;" ^
        "    } catch {" ^
        "        Invoke-WebRequest -Uri $taggedRawUrl -OutFile $scriptPath -UseBasicParsing;" ^
        "    }" ^
        "    $shaUrl = \"https://github.com/MishaelOliva/browser-engine/releases/download/$releaseTag/Install-MishaWeb.ps1.sha256\";" ^
        "    $hasChecksumAsset = $false;" ^
        "    try {" ^
        "        Invoke-WebRequest -Uri $shaUrl -OutFile $shaPath -UseBasicParsing;" ^
        "        $hasChecksumAsset = $true;" ^
        "    } catch {" ^
        "        $hasChecksumAsset = $false;" ^
        "    }" ^
        "    if ($hasChecksumAsset) {" ^
        "        $shaLine = (Get-Content -LiteralPath $shaPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1);" ^
        "        if ($shaLine -notmatch '^([0-9a-fA-F]{64})(?:\s+\*?(.+))?$') { throw 'Malformed Install-MishaWeb.ps1.sha256 checksum file.'; }" ^
        "        $assetHash = $Matches[1].ToLowerInvariant();" ^
        "        if (-not [string]::IsNullOrWhiteSpace($expectedScriptSha256) -and ($expectedScriptSha256.Trim().ToLowerInvariant() -ne $assetHash)) {" ^
        "            throw 'ExpectedScriptSha256 does not match published Install-MishaWeb.ps1.sha256.';" ^
        "        }" ^
        "        $expectedScriptSha256 = $assetHash;" ^
        "    }" ^
        "    if (-not [string]::IsNullOrWhiteSpace($expectedScriptSha256)) {" ^
        "        $expectedNorm = $expectedScriptSha256.Trim().ToLowerInvariant();" ^
        "        if ($expectedNorm -notmatch '^[0-9a-f]{64}$') { throw 'ExpectedScriptSha256 must be a 64-character hex SHA-256 digest.'; }" ^
        "        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $scriptPath).Hash.ToLowerInvariant();" ^
        "        if ($actualHash -ne $expectedNorm) { throw \"Install-MishaWeb.ps1 SHA-256 mismatch. Expected $expectedNorm, got $actualHash.\"; }" ^
        "    }" ^
        "    $parseTokens = $null; $scriptErrors = $null;" ^
        "    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$parseTokens, [ref]$scriptErrors);" ^
        "    if ($scriptErrors -and $scriptErrors.Count -gt 0) { throw ('Downloaded Install-MishaWeb.ps1 has syntax errors: ' + ($scriptErrors -join '; ')); }" ^
        "    & $scriptPath @forwardedNamed @forwardedPositional;" ^
        "} finally {" ^
        "    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue;" ^
        "}"
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Installation exited with error code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

exit /b 0
