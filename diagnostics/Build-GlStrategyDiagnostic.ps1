# Isolated unsigned x86 diagnostic; no production exporter behavior is changed.
param([string]$Repository='C:\src\InvoiceWindowBuild', [string]$Destination='C:\src\GlStrategyDiagnosticV1')
$ErrorActionPreference='Stop'
if (Test-Path $Destination) { throw 'Use a fresh diagnostic destination.' }
robocopy $Repository $Destination /E /XD .git bin obj artifacts docs e2e-secrets results Sage50ConnectorSetup Sage50ConnectorSetupCustomActions /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Diagnostic copy failed.' }
$program=Join-Path $Destination 'Program.cs'
$source=[IO.File]::ReadAllText($program).Replace("`r`n","`n")
$needle='public static int Main(string[] args)' + "`n        {"
$insert=$needle + "`n            if (args.Length == 3 && args[0] == `"--generate-gl-csv-adverse`") return Diagnostics.GlCsvStrategyBenchmark.RunGenerateAdverse(args);`n            if (args.Length == 4 && args[0] == `"--generate-gl-csv`") return Diagnostics.GlCsvStrategyBenchmark.RunGenerate(args);`n            if (args.Length == 4 && args[0] == `"--capture-gl-csv`") return Diagnostics.GlExportDiagnostic.Run(args);`n            if (args.Length >= 4 && args[0] == `"--benchmark-gl-csv`") return Diagnostics.GlCsvStrategyBenchmark.Run(args);"
if (-not $source.Contains($needle)) { throw 'Entry point did not match.' }
[IO.File]::WriteAllText($program,$source.Replace($needle,$insert),(New-Object Text.UTF8Encoding($true)))
$exporter=Join-Path $Destination 'Helpers\GeneralLedgerExporter.cs'
$source=[IO.File]::ReadAllText($exporter)
$needle='List<GlTransactionLineBody> allLines = ParseCsv(csvPath);'
$insert='if (Diagnostics.GlExportDiagnostic.CapturePath != null) { File.Copy(csvPath, Diagnostics.GlExportDiagnostic.CapturePath, false); return new List<GlTransactionBody>(); }' + "`r`n                " + $needle
if (-not $source.Contains($needle)) { throw 'CSV capture hook did not match.' }
[IO.File]::WriteAllText($exporter,$source.Replace($needle,$insert),(New-Object Text.UTF8Encoding($true)))
$project=Join-Path $Destination 'Sage50Connector.csproj'
$source=[IO.File]::ReadAllText($project).Replace('<Compile Include="Program.cs" />','<Compile Include="Program.cs" /><Compile Include="diagnostics\GlExportDiagnostic.cs" /><Compile Include="diagnostics\GlCsvStrategyBenchmark.cs" />')
[IO.File]::WriteAllText($project,$source,(New-Object Text.UTF8Encoding($true)))
& 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe' $project /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x86 /m /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw 'GL diagnostic build failed.' }
Write-Output ('UNSIGNED GL DIAGNOSTIC BUILD OK: '+(Join-Path $Destination 'bin\Release\Sage50Connector.exe'))
