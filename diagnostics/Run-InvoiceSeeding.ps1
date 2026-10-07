param(
    [int]$StartNumber = 1,
    [int]$Count = 1,
    [string]$Company = 'Rutter Test Co',
    [string]$Destination = 'C:\src\Sage50InvoicePagingDiagnosticV3',
    [string]$CandidateDate,
    [int]$TimeoutMinutes = 60,
    [int]$Attempt = 1
)
$ErrorActionPreference = 'Stop'
$taskName = 'Sage50InvoiceSeed_' + $StartNumber + '_' + $Count + '_' + $Attempt
$exe = Join-Path $Destination 'bin\Release\Sage50Connector.exe'
$resultDirectory = Join-Path $Destination 'results'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$result = Join-Path $resultDirectory ('seed-' + $StartNumber + '-' + $Count + '-attempt-' + $Attempt + '.json')
# The seeder resumes by skipping existing tagged Sage invoice references.
$arguments = '--seed-invoices "' + $Company + '" ' + $StartNumber + ' ' + $Count + ' "' + $result + '"'
if ($CandidateDate) { $arguments += ' ' + $CandidateDate }
$action = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $Destination
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 5
    $task = Get-ScheduledTask -TaskName $taskName
    if ($task.State -eq 'Ready' -and (Test-Path $result)) { break }
} while ([DateTime]::UtcNow -lt $deadline)
if ($task.State -ne 'Ready') { throw 'Seed task is still running at timeout; leave it running and inspect its progress manifest.' }
if (-not (Test-Path $result)) { throw 'Invoice seeding did not create a manifest before timeout.' }
$manifest = Get-Content $result -Raw | ConvertFrom-Json
if ($manifest.status -notin @('completed', 'period-inspection')) { throw ('Invoice seed ended with status ' + $manifest.status + '; createdCount=' + $manifest.createdCount) }
$taskInfo = Get-ScheduledTaskInfo -TaskName $taskName
if ($taskInfo.LastTaskResult -ne 0) { throw ('Invoice diagnostic exited with code ' + $taskInfo.LastTaskResult + '; manifest=' + $result) }
Get-Content $result -Raw
