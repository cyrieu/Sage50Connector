# Reproduces a Sage GL export containing posting order 0 (seen in FatPipe's real
# company file, 2026-10-13) and checks the connector parses and groups it.
# No Sage access: loads the built connector and calls its internal parser.
# Must run in 32-bit PowerShell (the assembly is x86):
#   & "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -File diagnostics\Test-GlZeroPostingOrder.ps1
param(
    [string]$Fixture = 'C:\src\GlStrategyDiagnosticV1\fixtures\testco-native.csv',
    [string]$Exe = 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe',
    [string]$WorkDir = "$env:TEMP\gl-zero-postorder"
)
$ErrorActionPreference = 'Stop'
function Check($ok, $msg) { if (-not $ok) { throw "FAIL: $msg" }; "PASS: $msg" }

New-Item -ItemType Directory -Force $WorkDir | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$gl = $asm.GetType('Sage50Connector.Helpers.GeneralLedgerExporter')
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$parse = $gl.GetMethod('ParseCsv', $flags)
$group = $gl.GetMethod('GroupByPostingOrder', $flags)
$summ = $gl.GetMethod('SummarizeZeroPostingOrder', $flags)

# Baseline: unmodified export.
$base = $parse.Invoke($null, @($Fixture))
$baseGroups = $group.Invoke($null, [object[]]@(,$base))
"baseline rows=$($base.Count) transactions=$($baseGroups.Count)"
Check ($summ.Invoke($null, [object[]]@(,$base)) -eq $null) 'no summary when nothing has posting order 0'

# Zero out the posting order on chosen physical lines. The first column is an
# unquoted integer, so only lines that start "<digits>," are touched.
$lines = [IO.File]::ReadAllLines($Fixture)
$orders = @{}
for ($i = 1; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match '^(\d+),') { $po = $Matches[1]; if (-not $orders[$po]) { $orders[$po] = @() }; $orders[$po] += $i }
}
$multi = @($orders.Keys | Where-Object { $orders[$_].Count -ge 3 } | Sort-Object { [long]$_ })
$targets = @()
foreach ($po in $multi[0..2]) { $targets += $orders[$po][0] }   # one row from each of three entries
$targets += $orders[$multi[3]][0..1]                             # two rows from the same entry
foreach ($i in $targets) { $lines[$i] = $lines[$i] -replace '^\d+,', '0,' }
$mod = [string](Join-Path $WorkDir 'zero-postorder.csv')
[IO.File]::WriteAllLines($mod, $lines)

$rows = $parse.Invoke($null, @($mod))
Check ($rows.Count -eq $base.Count) "parse keeps every row ($($rows.Count))"
$zero = @($rows | Where-Object { $_.JournalPostOrder -eq 0 })
Check ($zero.Count -eq $targets.Count) "$($targets.Count) rows carry posting order 0"

$summary = $summ.Invoke($null, [object[]]@(,$rows))
"summary: $summary"
Check ($summary -like "*$($targets.Count) GL row(s) have posting order 0*") 'summary counts the rows'

$tx = $group.Invoke($null, [object[]]@(,$rows))
$ids = @($tx | ForEach-Object { $_.ID })
Check (($ids | Select-Object -Unique).Count -eq $ids.Count) 'transaction ids are unique'
Check (-not ($ids -contains 'gl:0')) 'no merged gl:0 transaction'
$single = @($tx | Where-Object { $_.ID -like 'gl:0:*' })
Check ($single.Count -eq $targets.Count) 'each posting-order-0 row is its own transaction'
Check (@($single | Where-Object { $_.Lines.Count -ne 1 -or $_.ID -ne ('gl:0:' + $_.Lines[0].ID) }).Count -eq 0) 'keyed by the row GL GUID'
$lineTotal = ($tx | ForEach-Object { $_.Lines.Count } | Measure-Object -Sum).Sum
Check ($lineTotal -eq $base.Count) "no row lost or duplicated ($lineTotal lines)"
Check ($tx.Count -eq $baseGroups.Count + $targets.Count) 'other transactions unchanged'

$neg = [string](Join-Path $WorkDir 'negative-postorder.csv')
$lines[$targets[0]] = $lines[$targets[0]] -replace '^0,', '-1,'
[IO.File]::WriteAllLines($neg, $lines)
try { $parse.Invoke($null, @($neg)) | Out-Null; throw 'FAIL: negative posting order accepted' }
catch { Check ($_.Exception.InnerException.Message -like '*not a non-negative integer*') 'negative posting order still fails closed' }

'GL ZERO POSTING ORDER TESTS PASSED'
