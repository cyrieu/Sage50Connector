# Owned Windows Sage lab only. Does not alter machine/user environment settings.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$OutputDirectory = (Join-Path $env:TEMP ('ConnectorSdkTest-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).ProviderPath
$output = (New-Item -ItemType Directory -Force $OutputDirectory).FullName
$reports = Join-Path $env:ProgramData 'Rutter\Sage50Connector\diagnostics'
$originalPath = $env:PATH
try {
    foreach ($scenario in @('baseline', 'without-actian-path', 'restored')) {
        $env:PATH = $originalPath
        if ($scenario -eq 'without-actian-path') {
            $env:PATH = ($originalPath.Split(';') | Where-Object { $_ -notmatch 'Actian|Pervasive|PSQL|PVSW' }) -join ';'
        }
        $started = Get-Date
        $process = Start-Process -FilePath $Executable -ArgumentList '--diagnose-sdk' -WorkingDirectory $output -PassThru
        $null = $process.Handle
        if (-not $process.WaitForExit(90000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'Diagnostic timed out; forced termination may leave a Sage session seat.'
        }
        $report = Get-ChildItem -LiteralPath $reports -Filter '*.json' |
            Where-Object { $_.Name -like ('*-' + $process.Id + '.json') -and $_.LastWriteTime -ge $started.AddSeconds(-1) } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $report) { throw 'No report in ProgramData; check LocalAppData fallback and permissions.' }
        $raw = Get-Content -LiteralPath $report.FullName -Raw
        $data = $raw | ConvertFrom-Json
        $attempts = @($data | Where-Object { $_.stage -match '^diagnostic-attempt-' })
        if ($attempts.Count -ne 2) { throw 'Expected two attempts in the same process.' }
        if ($process.ExitCode -ne 0) { throw ($scenario + ' unexpectedly failed.') }
        foreach ($attempt in $attempts) {
            if (@($attempt.companies).Count -lt 1 -or @($attempt.errors).Count -ne 0) { throw 'Healthy discovery failed.' }
        }
        if ($scenario -eq 'without-actian-path') {
            foreach ($stage in @('session-begin-failed', 'native-runtime-preloaded', 'session-begin-recovered')) {
                if (-not ($data | Where-Object { $_.stage -eq $stage })) { throw ('Missing recovery evidence: ' + $stage) }
            }
            if ($raw -notmatch 'System.DllNotFoundException' -or $raw -match 'uninitialized PeachtreeSession') {
                throw 'Expected original DLL failure followed by successful clean-session recovery.'
            }
        }
        Copy-Item -LiteralPath $report.FullName -Destination (Join-Path $output ($scenario + '.json'))
        Write-Output ($scenario + ': PASS; exit=' + $process.ExitCode)
        $process.Dispose()
    }
}
finally { $env:PATH = $originalPath }
Write-Output ('Evidence: ' + $output)
