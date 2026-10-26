param(
    [Parameter(Mandatory = $true)][string[]]$Package,
    [Parameter(Mandatory = $true)][string]$Report
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'This verification requires Microsoft Defender on Windows.' }
$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a new report file; previous scan evidence is not overwritten.' }
$status = Get-MpComputerStatus
if (!$status.AMServiceEnabled -or !$status.AntivirusEnabled) { throw 'Defender is unavailable. No clean result can be recorded.' }
$platformRoot = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
$scanner = Get-ChildItem -LiteralPath $platformRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (!$scanner) { $scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
if (!(Test-Path -LiteralPath $scanner -PathType Leaf)) { throw 'Defender scan tool missing.' }
$results = @()
$failed = $false
foreach ($requested in $Package) {
    $file = Get-Item -LiteralPath $requested
    if ($file.PSIsContainer -or $file.Length -eq 0) { throw 'Supply a nonempty final release archive.' }
    $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $started = [DateTime]::UtcNow
    # Custom scan with exclusions ignored, archives inspected, and no quarantine
    # or remediation. Nonzero results never become a clean verification pass.
    $output = (& $scanner -Scan -ScanType 3 -File $file.FullName -DisableRemediation 2>&1 | Out-String)
    $scanExit = $LASTEXITCODE
    $finished = [DateTime]::UtcNow
    $after = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $unchanged = $before -eq $after
    $complete = $scanExit -eq 0 -and $unchanged -and $output -match 'Scan finished' -and $output -notmatch 'failed|aborted|cancelled|canceled'
    $result = if ($complete) { 'completed_no_detections' } else { 'not_verified_clean' }
    if (!$complete) { $failed = $true }
    $current = Get-MpComputerStatus
    $results += [ordered]@{
        file = $file.Name; bytes = $file.Length; sha256 = $before; unchangedAfterScan = $unchanged
        startedUtc = $started.ToString('o'); finishedUtc = $finished.ToString('o')
        scanner = 'Microsoft Defender Antivirus'; engineVersion = $current.AMEngineVersion
        signatureVersion = $current.AntivirusSignatureVersion
        signaturesUpdatedUtc = $current.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o')
        scannerExitCode = $scanExit; result = $result
        scanType = 'custom_archive_no_remediation_exclusions_ignored'
    }
    Write-Output ($file.Name + ': ' + $result)
    if (!$complete) { Write-Warning ($output.Replace($file.FullName, $file.Name)) }
}
$reportData = [ordered]@{ schemaVersion = 1; result = $(if ($failed) { 'not_verified_clean' } else { 'completed_no_detections' }); packages = $results }
$parent = Split-Path -Parent $reportPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$reportData | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
if ($failed) { throw 'Release scan did not produce a verified clean completion. Review the scanner result.' }
