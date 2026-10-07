# Offline CSV-only tests; no Sage sessions or production jobs are started.
param([string]$root='C:\src\GlStrategyDiagnosticV1')
$ErrorActionPreference='Stop'
$exe=Join-Path $root 'bin\Release\Sage50Connector.exe'
$fixtures=Join-Path $root 'fixtures'
$out=Join-Path $root 'results\matrix'
New-Item -ItemType Directory -Force $out | Out-Null
function Invoke-Diagnostic($arguments,$name) {
$p=Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput (Join-Path $out ($name+'.stdout.txt')) -RedirectStandardError (Join-Path $out ($name+'.stderr.txt'))
Write-Output ($name+' exit='+$p.ExitCode)
}
Invoke-Diagnostic @('--generate-gl-csv-adverse',(Join-Path $fixtures 'testco-native.csv'),(Join-Path $fixtures 'adverse')) 'generate-adverse'
foreach ($copies in @(8,32)) {
Invoke-Diagnostic @('--generate-gl-csv',(Join-Path $fixtures 'testco-native.csv'),(Join-Path $fixtures ('scale-'+$copies+'.csv')),$copies) ('generate-'+$copies)
}
$modes=@('baseline','stream-dictionary','stream-groups','disk-spool')
foreach ($dataset in @('testco-native','scale-8','scale-32')) {
foreach ($mode in $modes) {
$name=$dataset+'-'+$mode
Invoke-Diagnostic @('--benchmark-gl-csv',(Join-Path $fixtures ($dataset+'.csv')),$mode,(Join-Path $out ($name+'.json'))) $name
}
}
foreach ($dataset in @('shuffled-groups','noncontiguous-group','line-order-reversed','date-include-boundaries','multiline-quoted-field')) {
foreach ($mode in $modes) {
$name=$dataset+'-'+$mode
$arguments=@('--benchmark-gl-csv',(Join-Path $fixtures ('adverse\'+$dataset+'.csv')),$mode,(Join-Path $out ($name+'.json')))
if ($dataset -eq 'date-include-boundaries') { $arguments+=@('2026-08-01','2026-09-01') }
Invoke-Diagnostic $arguments $name
}
}
foreach ($mode in $modes) {
$name='empty-window-'+$mode
Invoke-Diagnostic @('--benchmark-gl-csv',(Join-Path $fixtures 'testco-native.csv'),$mode,(Join-Path $out ($name+'.json')),'2026-09-01','2026-10-01') $name
}
Get-ChildItem $out -Filter '*.json' | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'summary.json')
