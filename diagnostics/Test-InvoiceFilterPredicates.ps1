# Pure LastSavedAt predicate checks; no company access or Sage writes.
param([string]$ConnectorPath='C:\src\InvoiceWindowDiagnosticFinal\bin\Release\Sage50Connector.exe')
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom($ConnectorPath)
$method=$assembly.GetType('Sage50Connector.Helpers.Sage50Repository').GetMethod('InModifiedWindow',[Reflection.BindingFlags]'NonPublic,Static')
$after=[DateTime]'2026-08-01'; $before=[DateTime]'2026-09-01'
$cases=@(
    @{saved=$null; missing=$true; expected=$true; name='null timestamp included in historical batch'},
    @{saved=$null; missing=$false; expected=$false; name='null timestamp excluded from recent batch'},
    @{saved=[DateTime]::MinValue; missing=$true; expected=$true; name='default timestamp follows missing policy'},
    @{saved=$after; missing=$false; expected=$true; name='updated_at boundary included'},
    @{saved=$after.AddTicks(-1); missing=$true; expected=$false; name='timestamp before updated_at excluded'},
    @{saved=$before.AddTicks(-1); missing=$false; expected=$true; name='timestamp before updated_before included'},
    @{saved=$before; missing=$true; expected=$false; name='updated_before boundary excluded'}
)
foreach($case in $cases) {
    $actual=$method.Invoke($null,[object[]]@($case.saved,$after,$before,$case.missing))
    if($actual -ne $case.expected) { throw ('FAIL: '+$case.name) }
    Write-Output ('PASS: '+$case.name)
}
Write-Output 'INVOICE FILTER PREDICATE TESTS PASSED'
