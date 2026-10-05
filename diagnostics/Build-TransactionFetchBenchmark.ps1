# Builds a separate development copy. Does not change the normal checkout or config.
param([string]$Repository = 'C:\src\Sage50Connector', [string]$Destination = 'C:\src\Sage50FetchBenchmark')
$ErrorActionPreference = 'Stop'
if (Test-Path $Destination) { throw 'Use a fresh destination for an isolated benchmark build.' }
robocopy $Repository $Destination /E /XD .git bin obj artifacts docs Sage50ConnectorSetup Sage50ConnectorSetupCustomActions /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Could not copy connector checkout.' }
$program = Join-Path $Destination 'Program.cs'
$source = [IO.File]::ReadAllText($program).Replace("`r`n", "`n")
$source = $source.Replace('public static int Main(string[] args)' + "`n        {", 'public static int Main(string[] args)' + "`n        {`n            if (args.Length == 2 && args[0] == `"--benchmark-fetch`") return Diagnostics.TransactionFetchBenchmark.Run(args[1]);")
if ($source -notmatch 'TransactionFetchBenchmark.Run') { throw 'Entry-point patch did not match.' }
[IO.File]::WriteAllText($program, $source, (New-Object Text.UTF8Encoding($true)))
$project = Join-Path $Destination 'Sage50Connector.csproj'
$xml = [IO.File]::ReadAllText($project).Replace('<Compile Include="Program.cs" />', '<Compile Include="Program.cs" /><Compile Include="diagnostics\TransactionFetchBenchmark.cs" />')
[IO.File]::WriteAllText($project, $xml, (New-Object Text.UTF8Encoding($true)))
& 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe' $project /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x86 /m /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
Write-Output ('BENCHMARK BUILD OK: ' + (Join-Path $Destination 'bin\Release\Sage50Connector.exe'))
