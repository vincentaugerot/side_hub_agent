# SideHub Agent Installer for Windows
# Requires: Node.js (for PTY terminal support)
#
#   SIDEHUB_SETUP_TOKEN  the agent token (optional): after installing, configure the current folder (the project) for
#                        the agent and start it (`sidehub-agent setup`). A failed install is reported to SideHub with it.
#                        Cleared from the session when the script ends, whether it succeeded or not.
#   SIDEHUB_INSTALL_DIR  install folder (default %LOCALAPPDATA%\Programs\sidehub-agent); an existing folder
#   is only replaced if it holds a previous agent install
#
# The archive is checked against the release's checksums.sha256, downloaded from GitHub Releases, and
# checksums.sha256 itself must carry a valid signature (checksums.sha256.sig) from the release key below:
# write access to the GitHub release alone is not enough to ship a tampered archive.

param(
    [string]$Version = "latest"
)

# Downloads live under /agent at the API host root (REST routes are under /api): accept either form
$SideHubApi = if ($env:SIDEHUB_API) { $env:SIDEHUB_API } else { "https://api.sidehub.io" }
$SideHubApi = $SideHubApi.TrimEnd('/') -replace '/api$', ''
$InstallDir = if ($env:SIDEHUB_INSTALL_DIR) { $env:SIDEHUB_INSTALL_DIR } else { "$env:LOCALAPPDATA\Programs\sidehub-agent" }
# Written in every install folder: proves a folder is ours before it is wiped on reinstall
$InstallMarker = ".sidehub-agent-install"
# Checksums come straight from GitHub Releases, not through the SideHub API that hands out the archive (a redirect
# to the release asset):
# a compromised API cannot hand out both a tampered archive and a matching checksum.
$GitHubRepo = if ($env:SIDEHUB_GITHUB_REPO) { $env:SIDEHUB_GITHUB_REPO } else { "vincentaugerot/side_hub_agent" }

# Release signing key (RSA, PKCS#1 v1.5 / SHA-256 over checksums.sha256), the private half is the
# RELEASE_SIGNING_KEY secret of the release workflow. Same key as install.sh (ReleaseSigningKeyTests).
$ReleaseSigningKeyXml = '<RSAKeyValue><Modulus>t9EUPG9pOyOv9va4UK8vI4mJA4nPN7GUiE+Cj7aek6OI3WSl1ma5Bdp7rPYj/aYX+XiM4FMGbzqpLwiYauZzCr4Xlf4Nka/3AKzvl7PorFXCnj1Y5aSw5A5t7loAPEwC6SIQoCvSFmMnDUcbOKjet/jhe2aHFk8AoKIhtD1OPP1fYunv9+pmFNYV3RaoTW4KqIvKviJlPOYiJF+eFu1k2yXQt9js+hzOBztXIlm3FKR9Te/mzEEbdrZo0SCh4r51yxR/n2Gn4SDlHBqSGxrHNqGSMmXNdqsyQuzb6Pu6fYIweRD7gW4gd7bLRDe7bFXHEI2VGlT+5hUEaAfl8FbooLPHu2P7i+UkiPrDzcehNCQ0LQyyyNEZIxEL63zHWCEsmiXBoRXpaJg94i1Y1Zzmnh4Q2DlpTCeVzgUGqgLGUaR4CiJdJRnFQlB+9Rm9UsRIJV5RihAW4R/fEGvBX2Nv0WKMxs9stdwcYmYrk19LJGLlFfpL9c5t9TF0KDB2SiIV</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
# Releases published before signing: installed on the checksum alone (with a warning). Any later release
# without a signature is refused, so deleting checksums.sha256.sig does not downgrade the check.
$LastUnsignedVersion = [version]"1.0.61"
# First release whose archive bundles pty-helper's node_modules (npm ci from the lockfile, in the CI).
# Older ones need `npm install` from the registry at install time, running package scripts: refused.
$MinVersion = [version]"1.0.59"
# From this release `sidehub-agent setup` reports its own failure, with the error it printed
$SetupReportsVersion = [version]"1.0.95"

# Failure reports: with a token in SIDEHUB_SETUP_TOKEN, a failed install tells SideHub why
# (POST /api/agent/diagnostics), so a stuck account shows the cause in SideHub. Only the first 16 characters of the
# token, the stage that failed and a short detail without user paths, the version being installed and the platform are
# sent (see README "Failure reports").
$SideHubFailReason = "install-failed"
$SideHubFailDetail = ""
$SideHubFailTag = ""
# The folder the command was run from: the project `sidehub-agent setup` configures
$SideHubProjectDir = (Get-Location -PSProvider FileSystem).ProviderPath
# pty-helper's node-pty needs it (the onboarding asks for it too)
$MinNodeMajor = 18

# The stage being run: what is reported if the install throws from here on
function Set-InstallStage {
    param([string]$Reason, [string]$Detail)
    $script:SideHubFailReason = $Reason
    $script:SideHubFailDetail = $Detail
}

function Send-InstallFailureReport {
    $token = "$env:SIDEHUB_SETUP_TOKEN".Trim()
    if (-not $script:SideHubFailReason -or $token.Length -lt 16) { return }
    $body = @{
        tokenPrefix  = $token.Substring(0, 16)
        reason       = $script:SideHubFailReason
        detail       = $script:SideHubFailDetail
        agentVersion = "install.ps1 $script:SideHubFailTag".Trim()
        os           = "windows-$env:PROCESSOR_ARCHITECTURE".ToLowerInvariant()
    } | ConvertTo-Json -Compress
    # Best-effort and bounded: never hold the user's terminal, never hide the install error
    try {
        Invoke-RestMethod -Method Post -Uri "$SideHubApi/api/agent/diagnostics" -ContentType "application/json" `
            -Body $body -TimeoutSec 5 -UseBasicParsing | Out-Null
    } catch { }
}

# Check Node.js
function Test-NodeJs {
    try {
        $nodeVersion = "$(& node --version 2>$null)".Trim()
    } catch {
        throw "Node.js is required but not installed. Install Node.js $MinNodeMajor or later from https://nodejs.org"
    }
    $major = 0
    if (-not ($nodeVersion -match '^v(\d+)\.' -and [int]::TryParse($Matches[1], [ref]$major)) -or $major -lt $MinNodeMajor) {
        $script:SideHubFailDetail = "node $nodeVersion found, $MinNodeMajor or later required"
        throw "Node.js $nodeVersion is too old: SideHub Agent needs Node.js $MinNodeMajor or later (https://nodejs.org)."
    }
    Write-Host "Node.js $nodeVersion found" -ForegroundColor Green
    return $true
}

function Get-Platform {
    $arch = if ([Environment]::Is64BitOperatingSystem) {
        if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
    } else {
        throw "32-bit architecture not supported"
    }
    return "win-$arch"
}

# Through the SideHub API (cached there), not the anonymous GitHub API: 60 requests an hour per IP, quickly
# exhausted behind a shared office IP. A wrong tag from it gains nothing: the archive must still match the
# signed checksums of that release, and versions below $MinVersion are refused.
function Get-LatestTag {
    try {
        $release = Invoke-RestMethod -Uri "$SideHubApi/agent/releases/latest" -UseBasicParsing
    } catch {
        Write-Host "SideHub API call failed: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
    if ($release.tag -match '^v\d') { return $release.tag }
    return $null
}

# Verify checksums.sha256 against its detached signature with the embedded release key
function Test-ChecksumsSignature {
    param([string]$ChecksumsPath, [string]$Tag, [string]$TempDir)

    $sigUrl = "https://github.com/$GitHubRepo/releases/download/$Tag/checksums.sha256.sig"
    $sigPath = Join-Path $TempDir "checksums.sha256.sig"
    try {
        Invoke-WebRequest -Uri $sigUrl -OutFile $sigPath -UseBasicParsing
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 404 -and [version]$Tag.TrimStart('v') -le $LastUnsignedVersion) {
            Write-Host "$Tag predates signed releases: only the SHA256 checksum is verified." -ForegroundColor Yellow
            return $true
        }
        Write-Host "Checksums signature not found ($sigUrl)" -ForegroundColor Red
        return $false
    }

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.FromXmlString($ReleaseSigningKeyXml)
        $valid = $rsa.VerifyData(
            [System.IO.File]::ReadAllBytes($ChecksumsPath),
            [System.IO.File]::ReadAllBytes($sigPath),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } finally {
        $rsa.Dispose()
    }
    if (-not $valid) {
        Write-Host "Invalid signature for checksums.sha256 ($Tag): release not published by SideHub" -ForegroundColor Red
        return $false
    }
    Write-Host "Release signature verified ($Tag)" -ForegroundColor Green
    return $true
}

# Refuse to wipe a folder that is not a previous agent install (a mistyped SIDEHUB_INSTALL_DIR, Program Files...)
function Test-InstallDir {
    if (-not [System.IO.Path]::IsPathRooted($InstallDir) -or $InstallDir -match '(^|[\\/])\.\.?([\\/]|$)') {
        Write-Host "SIDEHUB_INSTALL_DIR must be an absolute path without . or ..: $InstallDir" -ForegroundColor Red
        return $false
    }
    if (-not (Test-Path -LiteralPath $InstallDir)) { return $true }
    $item = Get-Item -LiteralPath $InstallDir -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        Write-Host "$InstallDir exists and is not a folder" -ForegroundColor Red
        return $false
    }
    # Empty, ours (marker), or an install from before the marker (agent binary + pty-helper)
    $isEmpty = -not (Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 1)
    $hasMarker = Test-Path -LiteralPath (Join-Path $InstallDir $InstallMarker)
    $isLegacyInstall = (Test-Path -LiteralPath (Join-Path $InstallDir "sidehub-agent.exe")) -and
        (Test-Path -LiteralPath (Join-Path $InstallDir "pty-helper"))
    if ($isEmpty -or $hasMarker -or $isLegacyInstall) { return $true }
    Write-Host "$InstallDir is not empty and holds no SideHub Agent install: aborting." -ForegroundColor Red
    $entries = Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 10 -ExpandProperty Name
    Write-Host "Found: $($entries -join ', ')" -ForegroundColor Red
    Write-Host "Pick another folder (SIDEHUB_INSTALL_DIR) or empty it yourself." -ForegroundColor Red
    return $false
}

# Verify the archive against the release's checksums.sha256; any failure aborts the install
function Test-ArchiveChecksum {
    param([string]$ArchivePath, [string]$AssetName, [string]$Tag, [string]$TempDir)

    $checksumsUrl = "https://github.com/$GitHubRepo/releases/download/$Tag/checksums.sha256"
    $checksumsPath = Join-Path $TempDir "checksums.sha256"
    Set-InstallStage "install-download-failed" "checksums.sha256 download from GitHub failed ($Tag)"
    try {
        Invoke-WebRequest -Uri $checksumsUrl -OutFile $checksumsPath -UseBasicParsing
    } catch {
        Write-Host "Unable to download checksums from ${checksumsUrl}: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    Set-InstallStage "install-verification-failed" "release signature or archive checksum invalid ($Tag)"
    if (-not (Test-ChecksumsSignature -ChecksumsPath $checksumsPath -Tag $Tag -TempDir $TempDir)) {
        return $false
    }

    # sha256sum format: "<hash>  <name>" (or "<hash> *<name>" in binary mode)
    $expected = $null
    foreach ($line in Get-Content $checksumsPath) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $AssetName) {
            $expected = $parts[0].ToLowerInvariant()
            break
        }
    }
    if (-not $expected) {
        Write-Host "No checksum for $AssetName in checksums.sha256 ($Tag)" -ForegroundColor Red
        return $false
    }

    $actual = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-Host "Checksum mismatch for ${AssetName}: expected $expected, got $actual" -ForegroundColor Red
        return $false
    }

    Write-Host "SHA256 checksum verified ($AssetName, $Tag)" -ForegroundColor Green
    return $true
}

# Processes running from the install folder: the agent, and pty-helper (node.exe loading its native modules
# from there). Windows locks their files, so the folder cannot be replaced while they run.
function Get-InstallDirProcesses {
    $root = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and (
            ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) -or
            ($_.CommandLine -and $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0))
    }
}

# Project folders (from ~\.sidehub\instances.json) whose agent is running, to restart them after the update
function Get-RunningAgentDirs {
    $registry = Join-Path $env:USERPROFILE ".sidehub\instances.json"
    if (-not (Test-Path -LiteralPath $registry)) { return @() }
    try {
        $entries = @(Get-Content -LiteralPath $registry -Raw | ConvertFrom-Json)
    } catch {
        return @()
    }
    foreach ($entry in $entries) {
        if (-not $entry.directory) { continue }
        $pidFile = Join-Path $entry.directory ".sidehub\run\sidehub-agent.pid"
        if (-not (Test-Path -LiteralPath $pidFile)) { continue }
        $agentPid = 0
        if ([int]::TryParse((Get-Content -LiteralPath $pidFile -Raw).Trim(), [ref]$agentPid) -and
            (Get-Process -Id $agentPid -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "sidehub-agent*" })) {
            $entry.directory
        }
    }
}

# Stop the agents running from the install folder; returns the project folders to restart afterwards
function Stop-RunningAgents {
    if (-not @(Get-InstallDirProcesses)) { return @() }
    $dirs = @(Get-RunningAgentDirs)
    if (-not $dirs) {
        Write-Host "No registered project found for the running agent: restart it yourself afterwards (sidehub-agent start -d)." -ForegroundColor Yellow
    }

    Write-Host "Stopping the running SideHub Agent for the update (its terminal sessions will end)..."
    $agentExe = Join-Path $InstallDir "sidehub-agent.exe"
    if (Test-Path -LiteralPath $agentExe) {
        & $agentExe stop --all | Out-Host
    }

    # Agents started by hand (not registered) or a stop that didn't finish: wait, then force
    for ($i = 0; $i -lt 20 -and @(Get-InstallDirProcesses); $i++) { Start-Sleep -Milliseconds 500 }
    foreach ($process in @(Get-InstallDirProcesses)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    for ($i = 0; $i -lt 10 -and @(Get-InstallDirProcesses); $i++) { Start-Sleep -Milliseconds 500 }

    $remaining = @(Get-InstallDirProcesses)
    if ($remaining) {
        throw ("Processes still running from ${InstallDir} (PID $(($remaining.ProcessId) -join ', ')): " +
            "stop them (sidehub-agent stop --all), then run the installer again.")
    }
    return $dirs
}

function Install-SideHubAgent {
    # Set here, not at script level: under irm | iex the script runs in the caller's scope, and these
    # would stay changed in the user's session. Called functions inherit them.
    $ErrorActionPreference = "Stop"
    # Windows PowerShell 5.1 redraws Invoke-WebRequest's progress bar on every chunk, which makes the
    # 70 MB archive download many times slower
    $ProgressPreference = "SilentlyContinue"

    Set-InstallStage "install-node-missing" "node not found in PATH"
    Test-NodeJs | Out-Null

    Set-InstallStage "install-failed" "unsupported platform"
    $platform = Get-Platform

    # Pin "latest" to a tag so the archive and its checksum come from the same release
    Set-InstallStage "install-download-failed" "couldn't resolve the latest release from SideHub"
    if ($Version -eq "latest") {
        $tag = Get-LatestTag
        if (-not $tag) {
            throw "Unable to resolve the latest version from $SideHubApi/agent/releases/latest"
        }
    } else {
        $tag = "v" + $Version.TrimStart('v')
    }
    # The tag goes into URLs and messages: a plain version only
    Set-InstallStage "install-failed" "invalid or too old version requested"
    if ($tag -notmatch '^v\d+(\.\d+){1,3}$') {
        throw "Invalid version: $Version (expected 1.0.61 or v1.0.61)"
    }
    if ([version]$tag.TrimStart('v') -lt $MinVersion) {
        throw ("$tag can no longer be installed: releases before v$MinVersion install their Node.js " +
            "dependencies from the npm registry at install time. Install v$MinVersion or later.")
    }

    $script:SideHubFailTag = $tag

    Set-InstallStage "install-failed" "install folder exists and holds something else than an agent install"
    if (-not (Test-InstallDir)) { throw "Installation aborted: unusable install folder ($InstallDir)." }

    $assetName = "sidehub-agent-$platform.zip"
    $url = "$SideHubApi/agent/download/$platform/$tag"

    if ($Version -eq "latest") {
        Write-Host "Downloading SideHub Agent $tag (latest, $platform)..."
    } else {
        Write-Host "Downloading SideHub Agent $tag ($platform)..."
    }

    # Unique temp directory: a fixed name could be pre-created (or swapped) by another process
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sidehub-agent-install-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    $restartDirs = @()

    try {
        $archivePath = Join-Path $tempDir "agent.zip"

        Set-InstallStage "install-download-failed" "archive download failed ($SideHubApi/agent/download/$platform/$tag)"
        try {
            Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
        } catch {
            throw "Unable to download from ${url}: $($_.Exception.Message)"
        }

        if (-not (Test-ArchiveChecksum -ArchivePath $archivePath -AssetName $assetName -Tag $tag -TempDir $tempDir)) {
            throw "Installation aborted: the archive was not extracted."
        }

        Set-InstallStage "install-failed" "archive extraction failed, or pty-helper dependencies missing from it"
        Write-Host "Extracting..."
        $extractDir = Join-Path $tempDir "package"
        Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force

        # node_modules ships prebuilt in the verified archive (npm ci from the lockfile, in the release CI):
        # the install never runs npm.
        if (-not (Test-Path (Join-Path $extractDir "pty-helper\node_modules\node-pty"))) {
            throw "The $tag archive does not bundle pty-helper's Node.js dependencies: aborting."
        }
        Write-Host "Node.js dependencies bundled in the archive"
        New-Item -ItemType File -Path (Join-Path $extractDir $InstallMarker) | Out-Null

        Write-Host "Installing to $InstallDir..."
        if (Test-Path -LiteralPath $InstallDir) {
            # A running agent locks its files: Remove-Item would fail halfway through the folder
            Set-InstallStage "install-failed" "couldn't stop the agent running from the install folder"
            $restartDirs = @(Stop-RunningAgents)
            Set-InstallStage "install-permission-denied" "couldn't replace the install folder"
            # Marker last: if a file is still locked, the folder stays recognisable as ours for the next run
            Get-ChildItem -LiteralPath $InstallDir -Force |
                Where-Object { $_.Name -ne $InstallMarker } |
                Remove-Item -Recurse -Force
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
        }
        Set-InstallStage "install-permission-denied" "couldn't write the install folder"
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

        Get-ChildItem -Path $extractDir -Force | Copy-Item -Destination $InstallDir -Recurse -Force
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($dir in $restartDirs) {
        Write-Host "Restarting the agent in $dir..."
        # Not "& ... | Out-Host": the daemon inherits the pipe's write end and never closes it, so
        # the pipeline (and the install) would wait forever. Start-Process -Wait would wait for the
        # daemon too (it waits for the whole process tree): wait for "start -d" alone.
        $starter = Start-Process -FilePath (Join-Path $InstallDir "sidehub-agent.exe") -ArgumentList "start", "-d" `
            -WorkingDirectory $dir -NoNewWindow -PassThru
        $starter.WaitForExit()
    }

    # Add to PATH if not already present (this session too, so sidehub-agent works right away)
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $pathChanged = $userPath -notlike "*$InstallDir*"
    if ($pathChanged) {
        Write-Host "Adding to user PATH..."
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
    }
    if ($env:Path -notlike "*$InstallDir*") { $env:Path = "$env:Path;$InstallDir" }

    Write-Host ""
    Write-Host "SideHub Agent $tag installed successfully!" -ForegroundColor Green
    Write-Host ""

    if ("$env:SIDEHUB_SETUP_TOKEN".Trim()) {
        Write-Host "Configuring the agent in $SideHubProjectDir..."
        Set-InstallStage "install-setup-failed" "sidehub-agent setup failed after the install"
        # Start-Process, not "& ...": the daemon setup starts would inherit the output pipe and hold the script (see
        # the restart above). setup reads the token from SIDEHUB_SETUP_TOKEN, inherited.
        $setup = Start-Process -FilePath (Join-Path $InstallDir "sidehub-agent.exe") -ArgumentList "setup" `
            -WorkingDirectory $SideHubProjectDir -NoNewWindow -PassThru
        $null = $setup.Handle # without it, ExitCode stays empty once the process has exited
        $setup.WaitForExit()
        if ($setup.ExitCode -ne 0) {
            # Exit code 1: setup ran and reported why, this generic report would replace its detail in SideHub
            if ($setup.ExitCode -eq 1 -and [version]$tag.TrimStart('v') -ge $SetupReportsVersion) {
                $script:SideHubFailReason = ""
            }
            throw "sidehub-agent setup failed (exit code $($setup.ExitCode)): see the messages above."
        }
        Write-Host ""
        Write-Host "To start it again each time you log on: sidehub-agent service install"
        Write-Host "Useful commands: sidehub-agent status, sidehub-agent doctor, sidehub-agent logs, sidehub-agent stop"
    } else {
        Write-Host "To get started, from your project folder:"
        Write-Host "  sidehub-agent setup --token-stdin   (then paste the token copied from SideHub)"
        Write-Host "  sidehub-agent service install       (start it again each time you log on)"
    }
    if ($pathChanged) {
        Write-Host ""
        Write-Host "sidehub-agent is on the PATH of this terminal; open a new one for the other terminals already open."
    }
}

try {
    Install-SideHubAgent
} catch {
    Send-InstallFailureReport
    throw
} finally {
    # The agent token must not outlive the install in the user's session, whatever happened
    Remove-Item Env:SIDEHUB_SETUP_TOKEN -ErrorAction SilentlyContinue
}
