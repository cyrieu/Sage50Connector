[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Guid]$CompanyGuid,

    [Parameter(Mandatory = $true)]
    [DateTime]$StartDate,

    [Parameter(Mandatory = $true)]
    [DateTime]$EndDate,

    [string]$OutputPath = "$env:ProgramData\Rutter\Sage50Connector\diagnostics\general-ledger-rows.json",

    [string]$CompareTo,

    [string]$CredentialPath = "$env:ProgramData\Rutter\Sage50Connector\diagnostics\sage-com-credential.xml"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($EndDate.Date -le $StartDate.Date) {
    throw 'EndDate must be later than StartDate. EndDate is exclusive.'
}

function Release-ComObject {
    param([object]$ComObject)
    if ($null -ne $ComObject -and [Runtime.InteropServices.Marshal]::IsComObject($ComObject)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($ComObject)
    }
}

function Read-Field {
    param(
        [psobject]$Row,
        [string[]]$Names
    )
    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties |
            Where-Object { ($_.Name -replace '[^A-Za-z0-9]', '') -eq ($name -replace '[^A-Za-z0-9]', '') } |
            Select-Object -First 1
        if ($null -ne $property) { return [string]$property.Value }
    }
    return $null
}

function Parse-Decimal {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return [decimal]0 }
    $styles = [Globalization.NumberStyles]::Number -bor [Globalization.NumberStyles]::AllowCurrencySymbol
    $parsed = [decimal]0
    if ([decimal]::TryParse($Value, $styles, [Globalization.CultureInfo]::CurrentCulture, [ref]$parsed)) {
        return $parsed
    }
    if ([decimal]::TryParse($Value, $styles, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }
    throw "Could not parse General Ledger amount '$Value'."
}

function Get-JournalTypeName {
    param([int]$JournalType)
    $names = @{
        0 = 'general'
        1 = 'cash_receipts'
        2 = 'payments'
        3 = 'sales'
        4 = 'purchases'
        5 = 'payroll'
        6 = 'cost_of_goods_sold'
        7 = 'inventory_adjustment'
        8 = 'assembly_adjustment'
        9 = 'below_zero_inventory'
    }
    if ($names.ContainsKey($JournalType)) { return $names[$JournalType] }
    return 'unknown'
}

$login = $null
$loginSelector = $null
$application = $null
$exporter = $null
$csvPath = $null
$previousCompanyGuid = $null
$previousCompanyWasOpen = $false
$openedTargetCompany = $false
$credential = $null
$plainPassword = $null

try {
    if (-not (Test-Path -LiteralPath $CredentialPath)) {
        throw "Sage COM credential not found at '$CredentialPath'. Run Set-GeneralLedgerComCredential.ps1 as the interactive Sage user."
    }
    $credential = Import-Clixml -LiteralPath $CredentialPath
    if ($credential.UserName -ne 'Peachtree') {
        throw "The Sage external-data user must be 'Peachtree'."
    }
    $plainPassword = $credential.GetNetworkCredential().Password
    if ([string]::IsNullOrWhiteSpace($plainPassword)) {
        throw 'The Sage external-data password cannot be blank.'
    }

    # Sage's COM sample uses Login.GetApplication followed by
    # Application.CreateExporter. Late binding keeps the diagnostic independent
    # of Sage's versioned Interop.PeachwServer assembly.
    # When Sage is already running, Sage's ManagedCOM sample obtains the login
    # object through LoginSelector. Creating a fresh Login object can return
    # E_ACCESSDENIED even for the same interactive Windows user.
    try {
        $loginSelector = New-Object -ComObject 'PeachtreeAccounting.LoginSelector'
        $login = $loginSelector.GetCurrentLoginObject()
    } catch {
        $login = $null
    }
    if ($null -eq $login) {
        $login = New-Object -ComObject 'PeachtreeAccounting.Login.33'
    }
    $application = $login.GetApplication($credential.UserName, $plainPassword)
    $plainPassword = $null

    $previousCompanyWasOpen = [bool]$application.CompanyIsOpen
    if ($previousCompanyWasOpen) {
        $previousCompanyGuid = [string]$application.CurrentCompanyGUID
    }

    if (-not $previousCompanyWasOpen -or
        -not [string]::Equals($previousCompanyGuid, $CompanyGuid.ToString(), [StringComparison]::OrdinalIgnoreCase)) {
        if ($previousCompanyWasOpen) { $application.CloseCompany() }
        $application.OpenCompanyByGUID($CompanyGuid.ToString())
        $openedTargetCompany = $true
    }

    # Numeric values are from the Sage 50 2026 type library. The public COM
    # contract has kept these values stable; using numbers is what permits late
    # binding without redistributing Sage's licensed interop assembly.
    $generalLedgerRowsObject = 16
    $dateFilterRange = 1
    $csvFileType = 0
    $overwriteWithoutAsking = 1
    $sortByJournalPostOrder = 0

    $exporter = $application.CreateExporter($generalLedgerRowsObject)
    $exporter.ClearExportFieldList()
    0..13 | ForEach-Object { $exporter.AddToExportFieldList([int16]$_) }
    $exporter.SetIncludeHeadersFlag(1)
    $exporter.SetSortField([int16]$sortByJournalPostOrder)
    $exporter.SetFileType($csvFileType)
    $exporter.SetFileExistsOption($overwriteWithoutAsking)

    $csvPath = Join-Path ([IO.Path]::GetTempPath()) ("sage50-gl-" + [Guid]::NewGuid() + '.csv')
    $exporter.SetFilename($csvPath)

    # Sage's exporter accepts an inclusive date range. Subtracting one day from
    # the exclusive upper bound gives the connector unambiguous half-open
    # semantics: StartDate <= posting date < EndDate.
    $exporter.SetDateFilterValue($dateFilterRange, $StartDate.Date, $EndDate.Date.AddDays(-1))
    $exporter.Export()

    $rawRows = @(Import-Csv -LiteralPath $csvPath)
    $rows = foreach ($raw in $rawRows) {
        $journalPostOrderText = Read-Field $raw @('JournalPostOrder', 'Journal Post Order')
        $journalRowIndexText = Read-Field $raw @('JournalRowIndex', 'Journal Row Index')
        $journalTypeText = Read-Field $raw @('Type')
        $includeInGlText = Read-Field $raw @('IncludeInGL', 'Include In GL')

        [pscustomobject]@{
            journalPostOrder = if ([string]::IsNullOrWhiteSpace($journalPostOrderText)) { $null } else { [long]$journalPostOrderText }
            journalRowIndex = if ([string]::IsNullOrWhiteSpace($journalRowIndexText)) { $null } else { [int]$journalRowIndexText }
            accountId = Read-Field $raw @('GLAccountId', 'GL Account ID')
            accountGuid = Read-Field $raw @('GLAccountGUID', 'GL Account GUID')
            date = Read-Field $raw @('Date')
            journalType = if ([string]::IsNullOrWhiteSpace($journalTypeText)) { $null } else { [int]$journalTypeText }
            reference = Read-Field $raw @('TransactionReference', 'Transaction Reference')
            description = Read-Field $raw @('Description')
            jobId = Read-Field $raw @('JobId', 'Job ID')
            jobGuid = Read-Field $raw @('JobGUID', 'Job GUID')
            amount = Parse-Decimal (Read-Field $raw @('TransactionAmount', 'Transaction Amount'))
            dateCleared = Read-Field $raw @('DateCleared', 'Date Cleared')
            id = Read-Field $raw @('GUID')
            includeInGL = $includeInGlText -match '^(?i:true|1|yes)$'
        }
    }

    $postingRows = @($rows | Where-Object { $_.includeInGL })
    $groups = @($postingRows | Group-Object journalPostOrder)
    $transactions = foreach ($group in $groups) {
        $ordered = @($group.Group | Sort-Object journalRowIndex, id)
        $first = $ordered[0]
        $types = @($ordered.journalType | Sort-Object -Unique)
        $dates = @($ordered.date | Sort-Object -Unique)
        $references = @($ordered.reference | Sort-Object -Unique)
        [pscustomobject]@{
            id = if ($null -eq $first.journalPostOrder) { $null } else { 'gl:' + $first.journalPostOrder }
            journalPostOrder = $first.journalPostOrder
            journalTypes = $types
            journalTypeNames = @($types | ForEach-Object { Get-JournalTypeName $_ })
            dates = $dates
            references = $references
            descriptions = @($ordered.description | Sort-Object -Unique)
            amount = [decimal](($ordered | Measure-Object amount -Sum).Sum)
            headerConsistent = $types.Count -eq 1 -and $dates.Count -eq 1 -and $references.Count -eq 1
            lines = $ordered
        }
    }

    $duplicatePostingOrdersAcrossTypes = @(
        $transactions | Where-Object { $_.journalTypes.Count -gt 1 } | Select-Object -ExpandProperty journalPostOrder
    )
    $duplicateRowGuids = @(
        $postingRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.id) } |
            Group-Object id | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name
    )

    $comparison = $null
    if (-not [string]::IsNullOrWhiteSpace($CompareTo)) {
        $prior = Get-Content -LiteralPath $CompareTo -Raw | ConvertFrom-Json
        $priorRowsById = @{}
        foreach ($priorTransaction in $prior.transactions) {
            foreach ($priorRow in $priorTransaction.lines) {
                if (-not [string]::IsNullOrWhiteSpace($priorRow.id)) { $priorRowsById[$priorRow.id] = $priorRow }
            }
        }
        $currentIds = @($postingRows.id | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $comparison = [pscustomobject]@{
            priorRowCount = $priorRowsById.Count
            currentRowCount = $currentIds.Count
            retainedRowGuids = @($currentIds | Where-Object { $priorRowsById.ContainsKey($_) }).Count
            addedRowGuids = @($currentIds | Where-Object { -not $priorRowsById.ContainsKey($_) })
            removedRowGuids = @($priorRowsById.Keys | Where-Object { $currentIds -notcontains $_ })
        }
    }

    $result = [pscustomobject]@{
        generatedAt = [DateTime]::UtcNow.ToString('o')
        companyGuid = $CompanyGuid.ToString()
        startDateInclusive = $StartDate.Date.ToString('yyyy-MM-dd')
        endDateExclusive = $EndDate.Date.ToString('yyyy-MM-dd')
        exporterDateEndInclusive = $EndDate.Date.AddDays(-1).ToString('yyyy-MM-dd')
        rawRowCount = $rows.Count
        postingRowCount = $postingRows.Count
        excludedRowCount = @($rows | Where-Object { -not $_.includeInGL }).Count
        transactionCount = $transactions.Count
        missingPostingOrderCount = @($postingRows | Where-Object { $null -eq $_.journalPostOrder }).Count
        zeroPostingOrderCount = @($postingRows | Where-Object { $_.journalPostOrder -eq 0 }).Count
        missingRowGuidCount = @($postingRows | Where-Object { [string]::IsNullOrWhiteSpace($_.id) }).Count
        duplicateRowGuids = $duplicateRowGuids
        duplicatePostingOrdersAcrossTypes = $duplicatePostingOrdersAcrossTypes
        inconsistentPostingOrders = @($transactions | Where-Object { -not $_.headerConsistent } | Select-Object -ExpandProperty journalPostOrder)
        journalTypes = @($postingRows | Group-Object journalType | Sort-Object Name | ForEach-Object {
            [pscustomobject]@{
                value = [int]$_.Name
                name = Get-JournalTypeName ([int]$_.Name)
                rowCount = $_.Count
                amount = [decimal](($_.Group | Measure-Object amount -Sum).Sum)
            }
        })
        comparison = $comparison
        transactions = @($transactions | Sort-Object journalPostOrder)
    }

    $directory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

    $result | Select-Object generatedAt, startDateInclusive, endDateExclusive, rawRowCount,
        postingRowCount, excludedRowCount, transactionCount, missingPostingOrderCount,
        zeroPostingOrderCount, missingRowGuidCount, duplicateRowGuids,
        duplicatePostingOrdersAcrossTypes, inconsistentPostingOrders, journalTypes, comparison |
        ConvertTo-Json -Depth 8
    Write-Output ('Diagnostic output: ' + $OutputPath)
}
finally {
    $plainPassword = $null
    $credential = $null
    Release-ComObject $exporter
    if ($null -ne $application -and $openedTargetCompany) {
        try { $application.CloseCompany() } catch { Write-Warning $_.Exception.Message }
        if ($previousCompanyWasOpen -and -not [string]::IsNullOrWhiteSpace($previousCompanyGuid)) {
            try { $application.OpenCompanyByGUID($previousCompanyGuid) } catch { Write-Warning $_.Exception.Message }
        }
    }
    Release-ComObject $application
    Release-ComObject $login
    Release-ComObject $loginSelector
    if ($null -ne $csvPath -and (Test-Path -LiteralPath $csvPath)) {
        Remove-Item -LiteralPath $csvPath -Force
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
