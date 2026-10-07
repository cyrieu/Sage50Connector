Write-Output 'Sage/Actian service inventory:'
Get-Service |
    Where-Object { $_.Name -match 'Sage|Peachtree|Actian|Pervasive|PSQL' -or $_.DisplayName -match 'Sage|Peachtree|Actian|Pervasive|PSQL' } |
    Select-Object Name, DisplayName, Status |
    Format-Table -AutoSize

Write-Output 'Sage/Actian process inventory:'
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -match 'Sage|Peach|Actian|Pervasive|PSQL|w3db' } |
    Select-Object ProcessId, Name, ExecutablePath |
    Format-Table -AutoSize

Write-Output 'Owned invoice diagnostic task inventory:'
Get-ScheduledTask -TaskName 'Sage50InvoiceIngestE2E','Sage50InvoiceIngestRestart' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $info = Get-ScheduledTaskInfo -TaskName $_.TaskName
        [pscustomobject]@{ Name=$_.TaskName; State=$_.State; LastResult=$info.LastTaskResult }
    } | Format-Table -AutoSize
