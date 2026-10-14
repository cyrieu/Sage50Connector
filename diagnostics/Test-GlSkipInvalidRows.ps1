# Checks that invalid GL rows are skipped (not fatal) and reported masked, and
# that a mostly-invalid export still fails. No Sage access: loads the built
# connector and calls its internal parser. Run in 32-bit PowerShell on the VM:
#   & "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -File diagnostics\Test-GlSkipInvalidRows.ps1
param(
    [string]$Fixture = 'C:\src\GlStrategyDiagnosticV1\fixtures\testco-native.csv',
    [string]$Exe = 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe',
    [string]$WorkDir = "$env:TEMP\gl-skip-invalid"
)
$ErrorActionPreference = 'Stop'
function Check($ok, $msg) { if (-not $ok) { throw "FAIL: $msg" }; "PASS: $msg" }

New-Item -ItemType Directory -Force $WorkDir | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$gl = $asm.GetType('Sage50Connector.Helpers.GeneralLedgerExporter')
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$parse = $gl.GetMethod('ParseCsv', $flags)
$group = $gl.GetMethod('GroupByPostingOrder', $flags)

$base = $parse.Invoke($null, @($Fixture, $null))
"baseline rows=$($base.Count)"

# Columns: 3 = Transaction Date, 8 = Amount, 10 = GL GUID. Only quote-free lines
# are edited so splitting on commas is exact.
$lines = [IO.File]::ReadAllLines($Fixture)
$plain = @(1..($lines.Length - 1) | Where-Object { $lines[$_] -notmatch '"' -and $lines[$_] -match '^\d+,' })
function Corrupt([int]$i, [int]$col, [string]$value) { $f = $lines[$i].Split(','); $f[$col] = $value; $lines[$i] = $f -join ',' }
Corrupt $plain[10] 3 'Acme-not-a-date'
Corrupt $plain[20] 8 'Acme12x'
Corrupt $plain[30] 10 ''
$bad = [string](Join-Path $WorkDir 'three-invalid.csv')
[IO.File]::WriteAllLines($bad, $lines)

try { $parse.Invoke($null, @($bad, $null)) | Out-Null; throw 'FAIL: strict parse accepted invalid rows' }
catch { Check ($_.Exception.InnerException -ne $null) 'strict parse (no skip list) still throws' }

$skipped = New-Object 'System.Collections.Generic.List[string]'
$rows = $parse.Invoke($null, @($bad, $skipped))
Check ($skipped.Count -eq 3) 'three invalid rows skipped'
Check ($rows.Count -eq $base.Count - 3) "every other row kept ($($rows.Count))"
$summary = $gl.GetMethod('SummarizeSkippedRows', $flags).Invoke($null, @($skipped, $rows.Count))
"summary: $summary"
Check ($summary -like 'TRANSACTIONS: skipped 3 invalid GL row(s)*') 'summary counts skipped rows'
Check ($summary -notlike '*Acme*') 'summary masks the offending values'
$tx = $group.Invoke($null, [object[]]@(,$rows))
$ids = @($tx | ForEach-Object { $_.ID })
Check (($ids | Select-Object -Unique).Count -eq $ids.Count) 'transaction ids are unique'

# Mostly invalid: break the amount on every quote-free line.
foreach ($i in $plain) { Corrupt $i 8 'Acme12x' }
$most = [string](Join-Path $WorkDir 'mostly-invalid.csv')
[IO.File]::WriteAllLines($most, $lines)
$skipped = New-Object 'System.Collections.Generic.List[string]'
try { $parse.Invoke($null, @($most, $skipped)) | Out-Null; throw 'FAIL: mostly-invalid export accepted' }
catch { Check ($_.Exception.InnerException.Message -like '*More than half the rows are invalid*') 'mostly-invalid export still fails' }

'GL SKIP INVALID ROWS TESTS PASSED'
