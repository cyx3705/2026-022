function Invoke-DirectoryPromotion {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$Quarantine,
        [Parameter(Mandatory = $true)][scriptblock]$ValidateCandidate,
        [scriptblock]$AfterBackup
    )

    foreach ($unusedPath in @($Backup, $Quarantine)) {
        if (Test-Path -LiteralPath $unusedPath) {
            throw "Promotion transaction path already exists: $unusedPath"
        }
    }
    if (-not (Test-Path -LiteralPath $Candidate -PathType Container)) {
        throw "Promotion candidate is missing: $Candidate"
    }

    $candidatePromoted = $false
    try {
        & $ValidateCandidate $Candidate
        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $Backup
        }
        if ($null -ne $AfterBackup) {
            & $AfterBackup
        }
        Move-Item -LiteralPath $Candidate -Destination $Destination
        $candidatePromoted = $true
        & $ValidateCandidate $Destination
    }
    catch {
        $promotionError = $_
        $rollbackErrors = [Collections.Generic.List[string]]::new()

        try {
            if ($candidatePromoted -and (Test-Path -LiteralPath $Destination)) {
                Move-Item -LiteralPath $Destination -Destination $Quarantine
            }
            elseif (Test-Path -LiteralPath $Candidate) {
                Move-Item -LiteralPath $Candidate -Destination $Quarantine
            }
        }
        catch {
            $rollbackErrors.Add("quarantine failed: $($_.Exception.Message)")
        }

        try {
            if (Test-Path -LiteralPath $Backup) {
                if (Test-Path -LiteralPath $Destination) {
                    throw "destination still exists"
                }
                Move-Item -LiteralPath $Backup -Destination $Destination
            }
        }
        catch {
            $rollbackErrors.Add("restore failed: $($_.Exception.Message)")
        }

        if ($rollbackErrors.Count -gt 0) {
            throw "Promotion failed: $($promotionError.Exception.Message); rollback incomplete: $($rollbackErrors -join '; ')"
        }
        throw $promotionError
    }
}
