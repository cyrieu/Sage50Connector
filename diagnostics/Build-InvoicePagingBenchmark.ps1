# Build a stand-alone unsigned diagnostic copy; leave the VM checkout untouched.
param(
    [string]$Repository = 'C:\src\Sage50Connector',
    [string]$Destination = 'C:\src\Sage50InvoicePagingDiagnosticV3'
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Destination) { throw 'Use a fresh destination for an isolated diagnostic build.' }
robocopy $Repository $Destination /E /XD .git bin obj artifacts docs e2e-secrets results Sage50ConnectorSetup Sage50ConnectorSetupCustomActions /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Could not copy connector checkout.' }

$program = Join-Path $Destination 'Program.cs'
$source = [IO.File]::ReadAllText($program).Replace("`r`n", "`n")
$needle = 'public static int Main(string[] args)' + "`n        {"
$insert = $needle + "`n            if (args.Length == 3 && args[0] == `"--e2e-invoice-fetch`") return Diagnostics.InvoiceIngestE2E.Run(args);" + "`n            if (args.Length == 3 && args[0] == `"--compare-invoice-filters`") return Diagnostics.InvoiceFilterComparison.Run(args);" + "`n            if (args.Length == 1 && args[0] == `"--test-invoice-reader`") return Diagnostics.InvoiceWindowReaderTests.Run();" + "`n            if (args.Length >= 4 && args[0] == `"--benchmark-invoices`") return Diagnostics.InvoicePagingBenchmark.Run(args);`n            if ((args.Length == 5 || args.Length == 6) && args[0] == `"--seed-invoices`") return Diagnostics.InvoiceSeedingDiagnostic.Run(args);"
$source = $source.Replace($needle, $insert)
if ($source -notmatch 'InvoicePagingBenchmark.Run') { throw 'Entry-point patch did not match.' }
[IO.File]::WriteAllText($program, $source, (New-Object Text.UTF8Encoding($true)))

$project = Join-Path $Destination 'Sage50Connector.csproj'
$xml = [IO.File]::ReadAllText($project).Replace('<Compile Include="Program.cs" />', '<Compile Include="Program.cs" /><Compile Include="diagnostics\InvoicePagingBenchmark.cs" /><Compile Include="diagnostics\InvoiceSeedingDiagnostic.cs" /><Compile Include="diagnostics\InvoiceWindowReaderTests.cs" /><Compile Include="diagnostics\InvoiceFilterComparison.cs" /><Compile Include="diagnostics\InvoiceIngestE2E.cs" />')
[IO.File]::WriteAllText($project, $xml, (New-Object Text.UTF8Encoding($true)))
& 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe' $project /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x86 /m /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed.' }
Write-Output ('INVOICE DIAGNOSTIC BUILD OK: ' + (Join-Path $Destination 'bin\Release\Sage50Connector.exe'))
