param(
    [string]$Destination = 'C:\src\InvoiceWindowOvernightV3',
    [int]$NextNumber = 654,
    [int[]]$Checkpoints = @(652, 10000, 30000, 60000),
    [int]$BatchSize = 1000
)
$ErrorActionPreference = 'Stop'
if ($Destination -notmatch '^C:\\src\\InvoiceWindow') { throw 'Use an isolated invoice lab build.' }
$results = Join-Path $Destination 'results'
New-Item -ItemType Directory -Force $results | Out-Null
$statePath = Join-Path $results 'scale-pipeline.json'
$started = [DateTime]::UtcNow.ToString('o')
function Write-State($phase, $extra) {
    $state = @{ phase=$phase; startedUtc=$started; updatedUtc=[DateTime]::UtcNow.ToString('o'); nextNumber=$script:NextNumber; checkpoints=$Checkpoints }
    foreach ($key in $extra.Keys) { $state[$key]=$extra[$key] }
    $temporary = $statePath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Depth 10))
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}
try {
    foreach ($target in $Checkpoints) {
        while ($NextNumber -le $target + 1) {
            $count = [Math]::Min($BatchSize, $target + 2 - $NextNumber)
            Write-State 'seeding' @{targetInvoices=$target; batchStart=$NextNumber; batchCount=$count}
            & (Join-Path $Destination 'diagnostics\Run-InvoiceSeeding.ps1') -Destination $Destination -StartNumber $NextNumber -Count $count -CandidateDate '2026-08-15' -TimeoutMinutes 180 | Out-Null
            $NextNumber += $count
        }
        Write-State 'benchmarking' @{targetInvoices=$target}
        $before = @(Get-ChildItem $results -Filter 'production-windows-*.json' | Select-Object -ExpandProperty FullName)
        & (Join-Path $Destination 'diagnostics\Run-InvoicePagingBenchmark.ps1') -Destination $Destination -Mode production-windows -TaskName 'Sage50InvoiceScaleBenchmark' -TimeoutMinutes 240 | Out-Null
        $benchmark = Get-ChildItem $results -Filter 'production-windows-*.json' | Where-Object {$_.FullName -notin $before} | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $benchmark) { throw 'No new benchmark artifact found.' }
        $measurement = Get-Content $benchmark.FullName -Raw | ConvertFrom-Json
        # Mac controller performs the real Rutter ingest and DB verification.
        $ack = Join-Path $results ('e2e-ack-' + $target + '.json')
        Write-State 'awaiting-e2e' @{targetInvoices=$target; benchmark=$benchmark.FullName; ackPath=$ack}
        $deadline = [DateTime]::UtcNow.AddHours(8)
        while (-not (Test-Path $ack)) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Timed out waiting for local Rutter E2E verification; fixture remains intact.' }
            Start-Sleep -Seconds 15
        }
        $verification = Get-Content $ack -Raw | ConvertFrom-Json
        if ($verification.status -ne 'completed') { throw ('E2E checkpoint failed: ' + $ack) }
        Write-State 'checkpoint-completed' @{targetInvoices=$target; benchmark=$benchmark.FullName; verification=$verification}
    }
    Write-State 'completed' @{targetInvoices=$Checkpoints[-1]}
} catch {
    Write-State 'failed' @{error=$_.Exception.Message}
    exit 1
}
