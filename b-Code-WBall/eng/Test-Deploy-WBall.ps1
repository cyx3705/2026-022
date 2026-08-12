param(
    [string[]]$Suite = @('Fast')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Publish-Transaction.ps1')

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot '..'))
$PublishRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'b-Publish'))
$DeliveryRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot 'candidate'))
$HistoryRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot 'history'))
$WorkRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot 'work'))
$QuarantineRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot 'quarantine'))
$transactionId = [Guid]::NewGuid().ToString('N')
$BuildRoot = Join-Path $WorkRoot "dev-$transactionId"
$AppRoot = Join-Path $BuildRoot 'app'
$temporaryDelivery = Join-Path $WorkRoot "candidate-new-$transactionId"
$backupDelivery = Join-Path $WorkRoot "candidate-previous-$transactionId"
$quarantineDelivery = Join-Path $QuarantineRoot "candidate-failed-$transactionId"
$quarantineBuild = Join-Path $QuarantineRoot "build-failed-$transactionId"
$succeeded = $false
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

function Invoke-WBallSuite {
    param([string]$Name)
    switch ($Name) {
        'Fast' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallFastVerify\WBallFastVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore')
        }
        'Full' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore')
        }
        'RenderSmoke' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore', '--', '--render-smoke')
        }
        'PageSmoke' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore', '--', '--render-page-smoke')
        }
        'AssistFixes' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore', '--', '--assist-fixes')
        }
        'FriendlyAbsorbSmoke' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore', '--', '--friendly-absorb-smoke')
        }
        'GameplayFixes' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore', '--', '--gameplay-fixes')
        }
        'AssistPerformance' {
            Invoke-Dotnet @('run', '--project', 'b-Code-Verify\WBallVerify\WBallVerify.csproj',
                '-c', 'Debug', '--no-build', '--no-restore', '--', '--assist-performance')
        }
    }
}

$BuildRoot = Assert-UnderRoot $BuildRoot $WorkRoot 'development build'
$temporaryDelivery = Assert-UnderRoot $temporaryDelivery $WorkRoot 'temporary candidate'
$backupDelivery = Assert-UnderRoot $backupDelivery $WorkRoot 'candidate rollback'
$quarantineDelivery = Assert-UnderRoot $quarantineDelivery $QuarantineRoot 'failed candidate'
$quarantineBuild = Assert-UnderRoot $quarantineBuild $QuarantineRoot 'failed build'

$mutexInput = [Text.Encoding]::UTF8.GetBytes($ComponentRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexHash = (($sha256.ComputeHash($mutexInput) | ForEach-Object { $_.ToString('X2') }) -join '').Substring(0, 16)
}
finally {
    $sha256.Dispose()
}
$mutex = [Threading.Mutex]::new($false, "Local\WBall.Publish.$mutexHash")
$mutexAcquired = $false
try {
    $mutexAcquired = $mutex.WaitOne(0)
}
catch [Threading.AbandonedMutexException] {
    $mutexAcquired = $true
}
if (-not $mutexAcquired) {
    $mutex.Dispose()
    throw "Another WBall test deployment is already running for $ComponentRoot"
}

$allowedSuites = @('Fast', 'Full', 'RenderSmoke', 'PageSmoke', 'AssistFixes', 'FriendlyAbsorbSmoke', 'GameplayFixes', 'AssistPerformance')
$selectedSuites = @($Suite |
    ForEach-Object { $_ -split ',' } |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ } |
    Select-Object -Unique)
if ($selectedSuites.Count -eq 0) {
    throw 'At least one WBall verification suite is required'
}
$unknownSuites = @($selectedSuites | Where-Object { $_ -notin $allowedSuites })
if ($unknownSuites.Count -gt 0) {
    throw "Unknown WBall verification suite(s): $($unknownSuites -join ', '). Allowed: $($allowedSuites -join ', ')"
}

$env:DOTNET_EnableWriteXorExecute = '0'
Push-Location $RepoRoot
try {
    foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
        if (Test-Path -LiteralPath $transientRoot) {
            Remove-Item -LiteralPath $transientRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $transientRoot | Out-Null
    }
    New-Item -ItemType Directory -Force -Path $HistoryRoot | Out-Null

    Invoke-Dotnet @('restore', 'b-Code-WBall\WBall.sln', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'b-Code-WBall\WBall.sln', '-c', 'Debug', '--no-restore')
    foreach ($suiteName in $selectedSuites) {
        Invoke-WBallSuite $suiteName
    }

    New-Item -ItemType Directory -Force -Path $AppRoot | Out-Null
    Invoke-Dotnet @('restore', 'b-Code-WBall\App\App.csproj', '-r', 'win-x64', '-p:NuGetAudit=false')
    Invoke-Dotnet @('publish', 'b-Code-WBall\App\App.csproj', '-c', 'Debug', '-r', 'win-x64',
        '--self-contained', 'false', '--no-restore', '-o', $AppRoot)

    $exe = Join-Path $AppRoot 'WBall.exe'
    $appShell = Join-Path $AppRoot 'AppShell.Shell.dll'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'Debug test deployment did not produce WBall.exe'
    }
    if (-not (Test-Path -LiteralPath $appShell -PathType Leaf)) {
        throw 'Debug test deployment did not produce AppShell.Shell.dll'
    }

    [xml]$appProject = Get-Content -LiteralPath (Join-Path $ComponentRoot 'App\App.csproj') -Raw
    $version = [string](@($appProject.Project.PropertyGroup.Version | Where-Object { $_ })[-1])
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    $appShellVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appShell).ProductVersion
    if ([string]::IsNullOrWhiteSpace($version) -or
        -not $productVersion.StartsWith($version, [StringComparison]::Ordinal)) {
        throw "Published WBall version is $productVersion, expected $version"
    }
    if (-not $appShellVersion.StartsWith('3.0.3', [StringComparison]::Ordinal)) {
        throw "Published AppShell version is $appShellVersion, expected 3.0.3"
    }

    $sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    $sourceStatusArguments = @(
        '-C', $RepoRoot, 'status', '--porcelain', '--',
        'b-Code-WBall', 'b-Code-Verify', 'b-Office', 'README.md',
        'Directory.Build.props', 'nuget.config'
    )
    $sourceStatus = (& git @sourceStatusArguments) -join "`n"
    $report = [ordered]@{
        schemaVersion = 1
        product = 'WBall'
        version = $version
        channel = 'development'
        configuration = 'Debug'
        sourceCommit = $sourceCommit
        sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        suites = @($selectedSuites)
        sdk = (& dotnet --version).Trim()
        targetFramework = 'net8.0-windows'
        runtime = 'win-x64'
        selfContained = $false
        appShellVersion = $appShellVersion
        executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    }
    [IO.File]::WriteAllText(
        (Join-Path $AppRoot 'development-verification.json'),
        (($report | ConvertTo-Json -Depth 5) + "`n"),
        [Text.UTF8Encoding]::new($false))

    Copy-Item -LiteralPath $AppRoot -Destination $temporaryDelivery -Recurse
    $validate = {
        param($Root)
        $candidateExe = Join-Path $Root 'WBall.exe'
        $candidateShell = Join-Path $Root 'AppShell.Shell.dll'
        $candidateReport = Join-Path $Root 'development-verification.json'
        foreach ($requiredFile in @($candidateExe, $candidateShell, $candidateReport)) {
            if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
                throw "Development candidate is missing $([IO.Path]::GetFileName($requiredFile))"
            }
        }
        $metadata = [IO.File]::ReadAllText($candidateReport) | ConvertFrom-Json
        if ([string]$metadata.schemaVersion -ne '1' -or
            [string]$metadata.product -ne 'WBall' -or
            [string]$metadata.version -ne $version -or
            [string]$metadata.channel -ne 'development') {
            throw 'Development candidate verification report has invalid identity'
        }
        $candidateHash = (Get-FileHash -LiteralPath $candidateExe -Algorithm SHA256).Hash
        if ([string]$metadata.executableSha256 -ne $candidateHash) {
            throw 'Development candidate executable does not match its verification report'
        }
    }
    Invoke-DirectoryPromotion `
        $temporaryDelivery $DeliveryRoot $backupDelivery $quarantineDelivery $validate
    if (Test-Path -LiteralPath $backupDelivery) {
        Remove-Item -LiteralPath $backupDelivery -Recurse -Force
    }
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    Write-Host "Development-tested WBall candidate is ready at $DeliveryRoot"
    $succeeded = $true
}
catch {
    $failure = $_
    if (Test-Path -LiteralPath $BuildRoot) {
        Move-Item -LiteralPath $BuildRoot -Destination $quarantineBuild
    }
    throw $failure
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    $env:DOTNET_EnableWriteXorExecute = $previousWriteXorExecute
    if ($succeeded) {
        foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
            if (Test-Path -LiteralPath $transientRoot) {
                Remove-Item -LiteralPath $transientRoot -Recurse -Force
            }
        }
    }
    if ($mutexAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
