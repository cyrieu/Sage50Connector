param([string]$Company='Bellwether Garden Supply', [string]$Destination='C:\src\InvoiceWindowDiagnosticFinal')
$ErrorActionPreference='Stop'
$result=Join-Path $Destination ('results\filters-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'.json')
$action=New-ScheduledTaskAction -Execute (Join-Path $Destination 'bin\Release\Sage50Connector.exe') -Argument ('--compare-invoice-filters "'+$Company+'" "'+$result+'"') -WorkingDirectory $Destination
$principal=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName Sage50InvoiceFilterComparison -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName Sage50InvoiceFilterComparison
$deadline=[DateTime]::UtcNow.AddMinutes(5)
do { Start-Sleep 2; $task=Get-ScheduledTask -TaskName Sage50InvoiceFilterComparison } while($task.State -ne 'Ready' -and [DateTime]::UtcNow -lt $deadline)
if(!(Test-Path $result)) { throw 'No filter comparison result; check Sage approval.' }
Get-Content $result -Raw
if((Get-Content $result -Raw | ConvertFrom-Json).status -ne 'completed') { throw 'Filter comparison failed.' }
