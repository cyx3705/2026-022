param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Publish-Transaction.ps1')

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot '..'))
$PublishRoot = Join-Path $RepoRoot 'b-Publish'
$DeliveryRoot = Join-Path $PublishRoot 'candidate'
$HistoryRoot = Join-Path $PublishRoot 'history'
$WorkRoot = Join-Path $PublishRoot 'work'
$QuarantineRoot = Join-Path $PublishRoot 'quarantine'
$PackageRoot = Join-Path $RepoRoot 'z-Package'
$transactionId = [Guid]::NewGuid().ToString('N')
$BuildRoot = Join-Path $WorkRoot "release-$transactionId"
$AppRoot = Join-Path $BuildRoot 'app'
$CandidateNew = Join-Path $WorkRoot "candidate-new-$transactionId"
$CandidatePrevious = Join-Path $WorkRoot "candidate-previous-$transactionId"
$CandidateFailed = Join-Path $QuarantineRoot "candidate-failed-$transactionId"
$PackageNew = Join-Path $WorkRoot "package-new-$transactionId"
$PackagePrevious = Join-Path $WorkRoot "package-previous-$transactionId"
$PackageFailed = Join-Path $QuarantineRoot "package-failed-$transactionId"
$BuildFailed = Join-Path $QuarantineRoot "build-failed-$transactionId"
$succeeded = $false
$locationPushed = $false
$previousWriteXorExecute = $env:DOTNET_EnableWriteXorExecute

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-UnderRoot {
    param([string]$Path, [string]$Root, [string]$Name)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escaped its root: $fullPath"
    }
    return $fullPath
}

function Get-RelativePackagePath {
    param([string]$Root, [string]$Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package file escaped its root: $fullPath"
    }
    return $fullPath.Substring($fullRoot.Length).Replace('\', '/')
}

function Get-PackageFiles {
    param([string]$Root)
    $releaseRoot = [IO.Path]::GetFullPath((Join-Path $Root 'release')).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        -not $_.FullName.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object { Get-RelativePackagePath $Root $_.FullName })
}

function Write-PackageMetadata {
    param(
        [string]$Root,
        [string]$Version,
        [string]$Channel,
        [string]$SourceCommit,
        [bool]$SourceDirty,
        [string]$AppShellVersion
    )
    $releaseRoot = Join-Path $Root 'release'
    if (Test-Path -LiteralPath $releaseRoot) {
        Remove-Item -LiteralPath $releaseRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

    $artifacts = @(Get-PackageFiles $Root | ForEach-Object {
        $relative = Get-RelativePackagePath $Root $_.FullName
        [ordered]@{
            file = $relative
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'WBall'
        version = $Version
        channel = $Channel
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        sdk = (& dotnet --version).Trim()
        targetFramework = 'net8.0-windows'
        runtime = 'win-x64'
        selfContained = $false
        appShellVersion = $AppShellVersion
        artifacts = $artifacts
        packagedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $manifestPath = Join-Path $releaseRoot "$Version.json"
    $checksumPath = Join-Path $releaseRoot "$Version.sha256"
    [IO.File]::WriteAllText(
        $manifestPath,
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $checksumLines = @($artifacts | ForEach-Object { "$($_.sha256)  $($_.file)" })
    [IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))
}

function Assert-Package {
    param(
        [string]$Root,
        [string]$Version,
        [string]$Channel,
        [bool]$RequireClean
    )
    $manifestPath = Join-Path $Root "release\$Version.json"
    $checksumPath = Join-Path $Root "release\$Version.sha256"
    foreach ($required in @(
        (Join-Path $Root 'WBall.exe'),
        (Join-Path $Root 'AppShell.Shell.dll'),
        (Join-Path $Root 'ffmpeg\ffmpeg.exe'),
        (Join-Path $Root 'ffmpeg\NOTICE.md'),
        (Join-Path $Root 'ffmpeg\LICENSE'),
        $manifestPath,
        $checksumPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Package is missing $([IO.Path]::GetFileName($required))"
        }
    }

    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ([string]$manifest.schemaVersion -ne '1' -or
        [string]$manifest.product -ne 'WBall' -or
        [string]$manifest.version -ne $Version -or
        [string]$manifest.channel -ne $Channel) {
        throw 'Package manifest identity is invalid'
    }
    if ($RequireClean -and [bool]$manifest.sourceDirty) {
        throw 'Formal package manifest cannot describe dirty source'
    }

    $actual = @(Get-PackageFiles $Root)
    $expected = @($manifest.artifacts)
    if ($actual.Count -ne $expected.Count) {
        throw "Package file count is $($actual.Count), manifest expects $($expected.Count)"
    }
    $checksumLines = @([IO.File]::ReadAllLines($checksumPath) | Where-Object { $_ })
    if ($checksumLines.Count -ne $expected.Count) {
        throw 'Checksum file count does not match manifest'
    }
    $checksumMap = @{}
    foreach ($line in $checksumLines) {
        if ($line -notmatch '^([0-9A-F]{64})  (.+)$') {
            throw "Invalid checksum line: $line"
        }
        $checksumMap[$Matches[2]] = $Matches[1]
    }

    for ($index = 0; $index -lt $actual.Count; $index++) {
        $file = $actual[$index]
        $relative = Get-RelativePackagePath $Root $file.FullName
        $entry = $expected[$index]
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ([string]$entry.file -ne $relative -or
            [long]$entry.bytes -ne $file.Length -or
            [string]$entry.sha256 -ne $hash -or
            [string]$checksumMap[$relative] -ne $hash) {
            throw "Package artifact verification failed: $relative"
        }
    }

    $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Root 'WBall.exe')).ProductVersion
    $shellVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Root 'AppShell.Shell.dll')).ProductVersion
    if (-not $exeVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
        throw "Package WBall version is $exeVersion, expected $Version"
    }
    if (-not $shellVersion.StartsWith('3.0.3', [StringComparison]::Ordinal)) {
        throw "Package AppShell version is $shellVersion, expected 3.0.3"
    }
}

$BuildRoot = Assert-UnderRoot $BuildRoot $WorkRoot 'release build'
$CandidateNew = Assert-UnderRoot $CandidateNew $WorkRoot 'candidate staging'
$CandidatePrevious = Assert-UnderRoot $CandidatePrevious $WorkRoot 'candidate backup'
$CandidateFailed = Assert-UnderRoot $CandidateFailed $QuarantineRoot 'candidate quarantine'
$PackageNew = Assert-UnderRoot $PackageNew $WorkRoot 'package staging'
$PackagePrevious = Assert-UnderRoot $PackagePrevious $WorkRoot 'package backup'
$PackageFailed = Assert-UnderRoot $PackageFailed $QuarantineRoot 'package quarantine'
$BuildFailed = Assert-UnderRoot $BuildFailed $QuarantineRoot 'build quarantine'

$mutexInput = [Text.Encoding]::UTF8.GetBytes($ComponentRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexDigest = $sha256.ComputeHash($mutexInput)
    $mutexHash = ($mutexDigest | ForEach-Object { $_.ToString('X2') }) -join ''
}
finally {
    $sha256.Dispose()
}
$mutex = [Threading.Mutex]::new($false, "Local\WBall.Publish.$($mutexHash.Substring(0, 16))")
$mutexAcquired = $false
try {
    try { $mutexAcquired = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $mutexAcquired = $true }
    if (-not $mutexAcquired) {
        throw "Another WBall publication is already running for $ComponentRoot"
    }

    $env:DOTNET_EnableWriteXorExecute = '0'
    Push-Location $RepoRoot
    $locationPushed = $true
    foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
        if (Test-Path -LiteralPath $transientRoot) {
            Remove-Item -LiteralPath $transientRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $transientRoot | Out-Null
    }
    New-Item -ItemType Directory -Force -Path $HistoryRoot | Out-Null

    $sourceCommit = (& git rev-parse HEAD).Trim()
    $sourceStatus = (& git status --porcelain -- b-Code-WBall b-Code-Verify b-Office README.md `
        Directory.Build.props nuget.config .gitattributes .gitignore .ignore b-Publish/README.md) -join "`n"
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
    if ($Publish -and $sourceDirty) {
        throw 'Formal publication requires committed, clean WBall source and contracts'
    }

    Invoke-Dotnet @('restore', 'b-Code-WBall\WBall.sln', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'b-Code-WBall\WBall.sln', '-c', 'Debug', '--no-restore')
    Invoke-Dotnet @('build', 'b-Code-WBall\WBall.sln', '-c', 'Release', '--no-restore')
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallFastVerify\WBallFastVerify.csproj',
        '-c', 'Release', '--no-build', '--no-restore')
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
        '-c', 'Release', '--no-build', '--no-restore')

    New-Item -ItemType Directory -Force -Path $AppRoot | Out-Null
    Invoke-Dotnet @('restore', 'b-Code-WBall\App\App.csproj', '-r', 'win-x64', '-p:NuGetAudit=false')
    Invoke-Dotnet @('publish', 'b-Code-WBall\App\App.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'false', '--no-restore', '-o', $AppRoot)

    [xml]$appProject = Get-Content -LiteralPath (Join-Path $ComponentRoot 'App\App.csproj') -Raw
    $version = [string](@($appProject.Project.PropertyGroup.Version | Where-Object { $_ })[-1])
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'WBall version source is empty' }
    $shellPath = Join-Path $AppRoot 'AppShell.Shell.dll'
    if (-not (Test-Path -LiteralPath $shellPath -PathType Leaf)) { throw 'Release publish omitted AppShell.Shell.dll' }
    $appShellVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($shellPath).ProductVersion

    Copy-Item -LiteralPath $AppRoot -Destination $CandidateNew -Recurse
    Write-PackageMetadata $CandidateNew $version 'candidate' $sourceCommit $sourceDirty $appShellVersion
    $validateCandidate = { param($Root) Assert-Package $Root $version 'candidate' $false }
    Invoke-DirectoryPromotion $CandidateNew $DeliveryRoot $CandidatePrevious $CandidateFailed $validateCandidate
    if (Test-Path -LiteralPath $CandidatePrevious) {
        Remove-Item -LiteralPath $CandidatePrevious -Recurse -Force
    }
    Write-Host "Release-tested WBall candidate is ready at $DeliveryRoot"

    if ($Publish) {
        Copy-Item -LiteralPath $DeliveryRoot -Destination $PackageNew -Recurse
        Write-PackageMetadata $PackageNew $version 'package' $sourceCommit $false $appShellVersion
        $validatePackage = { param($Root) Assert-Package $Root $version 'package' $true }
        Invoke-DirectoryPromotion $PackageNew $PackageRoot $PackagePrevious $PackageFailed $validatePackage
        if (Test-Path -LiteralPath $PackagePrevious) {
            $historyName = "package-$version-$([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss'))"
            Move-Item -LiteralPath $PackagePrevious -Destination (Join-Path $HistoryRoot $historyName)
        }
        Write-Host "Formal WBall package is ready at $PackageRoot"
    }
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    $succeeded = $true
}
catch {
    $failure = $_
    if (Test-Path -LiteralPath $BuildRoot) {
        Move-Item -LiteralPath $BuildRoot -Destination $BuildFailed
    }
    throw $failure
}
finally {
    & dotnet build-server shutdown | Out-Null
    if ($locationPushed) { Pop-Location }
    $env:DOTNET_EnableWriteXorExecute = $previousWriteXorExecute
    if ($succeeded) {
        foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
            if (Test-Path -LiteralPath $transientRoot) {
                Remove-Item -LiteralPath $transientRoot -Recurse -Force
            }
        }
    }
    if ($mutexAcquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
