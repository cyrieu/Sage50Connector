Get-Service |
    Where-Object { $_.Name -match 'Sage|Peachtree' -or $_.DisplayName -match 'Sage|Peachtree' } |
    Select-Object Name, DisplayName, Status |
    Format-Table -AutoSize
