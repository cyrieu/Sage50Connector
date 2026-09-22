[CmdletBinding()]
param(
    [string]$ConnectorDirectory = "${env:ProgramFiles(x86)}\Rutter Sage 50 Connector",
    [string]$InstalledApiDirectory = "${env:ProgramFiles(x86)}\Sage\Peachtree\API",
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath('Desktop')) ('SageSdkDiagnostic-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 90,
    # Internal worker options. Each source must be tested in a fresh process.
    [switch]$Probe,
    [string]$SdkDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

function Write-FileInventory([string]$Path, [switch]$Hash) {
    try {
        $file = Get-Item -LiteralPath $Path -ErrorAction Stop
        $record = [ordered]@{ Path = $file.FullName; Exists = $true; Bytes = $file.Length; ModifiedUtc = $file.LastWriteTimeUtc.ToString('o') }
        if ($file.Extension -in @('.dll', '.exe')) { $record.Version = $file.VersionInfo.FileVersion }
        if ($Hash) { $record.SHA256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
        Write-Output ($record | ConvertTo-Json -Compress)
    }
    catch {
        Write-Output (([ordered]@{ Path = $Path; Status = 'Missing or inaccessible'; Error = $_.Exception.Message }) | ConvertTo-Json -Compress)
    }
}

function Write-CompanyInventory([object]$Company) {
    # Read only an explicit allowlist, not every SDK property / ToString().
    $record = [ordered]@{}
    foreach ($name in @('CompanyName', 'Guid', 'DatabaseName', 'ServerName', 'Path')) {
        try {
            $property = $Company.GetType().GetProperty($name)
            $record[$name] = if ($null -ne $property) { [string]$property.GetValue($Company, $null) } else { '(not exposed by this SDK)' }
        }
        catch { $record[$name] = '(unavailable: ' + $_.Exception.Message + ')' }
    }
    Write-Output ('Company: ' + ($record | ConvertTo-Json -Compress))
    # File metadata only. Never read company DATs, UDL credentials, or approval data.
    $folder = $record['Path']
    if (-not [string]::IsNullOrWhiteSpace($folder) -and -not $folder.StartsWith('(')) {
        foreach ($name in @('COMPANY.DAT', 'CrystalReports.udl', 'FILE.DDF', 'FIELD.DDF', 'APIACCSS.DAT')) {
            Write-FileInventory (Join-Path $folder $name)
        }
    }
}

if ($Probe) {
    $session = $null
    $stage = 'Preflight'
    $exitCode = 1
    function Write-Failure([Exception]$Exception) {
        while ($null -ne $Exception) {
            Write-Output ('{0}: {1} (HRESULT 0x{2:X8})' -f $Exception.GetType().FullName, $Exception.Message, $Exception.HResult)
            $Exception = $Exception.InnerException
        }
    }
    try {
        Write-Output ('UTC: ' + [DateTime]::UtcNow.ToString('o'))
        Write-Output ('Process bits: ' + ([IntPtr]::Size * 8))
        Write-Output ('SDK source: ' + $SdkDirectory)
        if ([IntPtr]::Size -ne 4) { throw 'This probe must run in 32-bit Windows PowerShell.' }
        $SdkDirectory = (Resolve-Path -LiteralPath $SdkDirectory).ProviderPath
        # Same working directory for both probes; do not add either SDK folder to PATH.
        # .NET may substitute GAC assemblies even when given an explicit path.
        # Report this rather than mistaking a shared assembly for a source comparison.
        foreach ($name in @('Sage.Peachtree.API.Resolver.dll', 'Sage.Peachtree.API.dll')) {
            $path = Join-Path $SdkDirectory $name
            $file = Get-Item -LiteralPath $path
            Write-Output ('File: {0}; version={1}; SHA256={2}' -f $file.FullName, $file.VersionInfo.FileVersion, (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
        }
        $stage = 'Initialize resolver'
        $resolver = [Reflection.Assembly]::LoadFrom((Join-Path $SdkDirectory 'Sage.Peachtree.API.Resolver.dll'))
        Write-Output ('Actual resolver: ' + $resolver.Location)
        $initializer = $resolver.GetType('Sage.Peachtree.API.Resolver.AssemblyInitializer', $true)
        [void]$initializer.GetMethod('Initialize', [Type[]]@()).Invoke($null, [object[]]@())
        $stage = 'Load API'
        $api = [Reflection.Assembly]::LoadFrom((Join-Path $SdkDirectory 'Sage.Peachtree.API.dll'))
        Write-Output ('Actual API: ' + $api.Location)
        if (-not [string]::Equals($api.Location, (Join-Path $SdkDirectory 'Sage.Peachtree.API.dll'), [StringComparison]::OrdinalIgnoreCase)) {
            Write-Output 'SOURCE COMPARISON: INCONCLUSIVE - .NET redirected the requested API. SDK health will still be tested.'
        }
        $stage = 'Create session'
        $sessionType = $api.GetType('Sage.Peachtree.API.PeachtreeSession', $true)
        $session = [Activator]::CreateInstance($sessionType)
        $stage = 'Begin session (sample-company diagnostic identity)'
        # No customer tokens or Rutter partner credentials. This probes enumeration,
        # not real-company licensing, RequestAccess, Open, or financial records.
        [void]$sessionType.GetMethod('Begin', [Type[]]@([string])).Invoke($session, [object[]]@(''))
        $stage = 'CompanyList'
        $companies = $sessionType.GetMethod('CompanyList', [Type[]]@()).Invoke($session, [object[]]@())
        $count = 0
        foreach ($company in $companies) { $count++; Write-CompanyInventory $company }
        Write-Output ('CompanyList succeeded; count=' + $count + '.')
        Write-Output 'RESULT: PASS - SDK initialized and enumerated companies.'
        $exitCode = 0
    }
    catch {
        Write-Output ('RESULT: FAIL at ' + $stage)
        Write-Failure $_.Exception
    }
    finally {
        if ($null -ne $session) {
            try { [void]$session.GetType().GetMethod('End', [Type[]]@()).Invoke($session, [object[]]@()) }
            catch { Write-Output 'Cleanup End failed:'; Write-Failure $_.Exception }
            if ($session -is [IDisposable]) {
                try { $session.Dispose() } catch { Write-Output 'Cleanup Dispose failed:'; Write-Failure $_.Exception }
            }
        }
        Write-Output 'Loaded Sage assemblies:'
        [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -like 'Sage*' } | ForEach-Object {
            Write-Output ('  {0}; {1}' -f $_.FullName, $_.Location)
        }
        Write-Output 'Loaded Sage/Actian native modules:'
        try {
            (Get-Process -Id $PID).Modules | Where-Object {
                $_.ModuleName -match '^(w3|btr|psql|zen|Sage|Peach)' -or $_.FileName -match '\\(Actian|Pervasive|PSQL|PVSW)\\'
            } | ForEach-Object { Write-Output ('  {0}; version={1}' -f $_.FileName, $_.FileVersionInfo.FileVersion) }
        }
        catch { Write-Output ('Module inventory unavailable: ' + $_.Exception.Message) }
    }
    exit $exitCode
}

if ($env:OS -ne 'Windows_NT') { throw 'Run this diagnostic on the affected Windows computer.' }
# Honor the MSI's selected installation directory unless explicitly overridden.
if (-not $PSBoundParameters.ContainsKey('ConnectorDirectory')) {
    foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Rutter\Sage50Connector', 'HKLM:\SOFTWARE\Rutter\Sage50Connector')) {
        $install = Get-ItemProperty -LiteralPath $key -Name InstallPath -ErrorAction SilentlyContinue
        if ($null -ne $install -and -not [string]::IsNullOrWhiteSpace($install.InstallPath)) {
            $ConnectorDirectory = $install.InstallPath
            break
        }
    }
}
$powershell = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $powershell)) {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
}
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
& {
    'Sage SDK diagnostic v2 - machine inventory'
    'UTC: ' + [DateTime]::UtcNow.ToString('o')
    'Windows: ' + [Environment]::OSVersion.VersionString
    '64-bit OS: ' + [Environment]::Is64BitOperatingSystem
    'PowerShell: ' + $PSVersionTable.PSVersion
    'Execution policies:'
    Get-ExecutionPolicy -List | ForEach-Object { '{0}: {1}' -f $_.Scope, $_.ExecutionPolicy }
    'Selected connector directory: ' + $ConnectorDirectory
    'Selected installed API directory: ' + $InstalledApiDirectory
    'Connector / API files (including missing expected files):'
    foreach ($name in @('Sage50Connector.exe', 'Sage50Connector.exe.config', 'Sage.Peachtree.API.dll', 'Sage.Peachtree.API.Resolver.dll')) {
        Write-FileInventory (Join-Path $ConnectorDirectory $name) -Hash
    }
    foreach ($name in @('Sage.Peachtree.API.dll', 'Sage.Peachtree.API.Resolver.dll')) {
        Write-FileInventory (Join-Path $InstalledApiDirectory $name) -Hash
    }
    'Connector state files: metadata only; contents are NOT collected.'
    foreach ($name in @('sage50Config.json', 'log.txt', 'sage-com-credential.bin', 'sage-com-authorization.json')) {
        Write-FileInventory (Join-Path $env:ProgramData ('Rutter\Sage50Connector\' + $name))
    }
    Write-FileInventory 'C:\Users\Default\Documents\sage50Config.json'
    'Sage configuration INI files: paths and metadata only.'
    $iniRoot = Join-Path $env:ProgramData 'Sage\Peachtree'
    Get-ChildItem -LiteralPath $iniRoot -Filter 'Peachtree*.ini' -File -ErrorAction SilentlyContinue | ForEach-Object { Write-FileInventory $_.FullName }
    'Sage/Actian files in known installation directories (not a whole-disk search):'
    $sageRoot = Split-Path -Parent $InstalledApiDirectory
    Get-ChildItem -LiteralPath $sageRoot -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '^(Sage.*\.dll|Peach.*\.(dll|exe))$'
    } | ForEach-Object { Write-FileInventory $_.FullName }
    $nativeRoots = @(
        "${env:ProgramFiles(x86)}\Actian\Zen\bin", "${env:ProgramFiles}\Actian\Zen\bin",
        "${env:ProgramFiles(x86)}\Pervasive Software\PSQL\bin", "${env:ProgramFiles(x86)}\Actian\PSQL\bin", 'C:\PVSW\bin'
    )
    foreach ($root in ($nativeRoots | Select-Object -Unique)) {
        foreach ($name in @('w3dbav90.dll', 'pscore6.dll', 'pscl6.dll', 'clientrb.dll', 'w3csp100.dll', 'w3dbsmgr.exe', 'zenengnsvc32.exe')) {
            $path = Join-Path $root $name
            if ($name -eq 'w3dbav90.dll' -or (Test-Path -LiteralPath $path)) { Write-FileInventory $path -Hash }
        }
    }
    'Relevant directories in inherited PATH (other entries omitted):'
    $env:PATH.Split(';') | Where-Object { $_ -match 'Sage|Peachtree|Actian|Pervasive|PSQL|PVSW' } | ForEach-Object {
        ([ordered]@{ Path = $_; Exists = (Test-Path -LiteralPath $_) }) | ConvertTo-Json -Compress
    }
    'Running connector/Sage/Actian processes: no command lines collected.'
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Sage|Peach|zeneng|w3dbsmgr' } | ForEach-Object {
        try { ([ordered]@{ Name = $_.ProcessName; Id = $_.Id; Path = $_.Path }) | ConvertTo-Json -Compress }
        catch { 'Process path unavailable: ' + $_.ProcessName }
    }
    'Sage/Actian services:'
    Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Sage|Peach|Actian|Pervasive|Zen' -or $_.DisplayName -match 'Sage|Peach|Actian|Pervasive' } | ForEach-Object {
        ([ordered]@{ Name = $_.Name; DisplayName = $_.DisplayName; Status = [string]$_.Status }) | ConvertTo-Json -Compress
    }
} | Out-File -LiteralPath (Join-Path $output 'inventory.txt') -Encoding UTF8 -Width 4096
$results = @()
foreach ($source in @(
    @{ Name = 'bundled'; Directory = $ConnectorDirectory },
    @{ Name = 'installed'; Directory = $InstalledApiDirectory }
)) {
    $log = Join-Path $output ($source.Name + '.txt')
    $stderr = Join-Path $output ($source.Name + '-stderr.txt')
    Write-Host ('Testing ' + $source.Name + ': ' + $source.Directory)
    # Encode a PowerShell command, not interpolated shell syntax. Literal quoting
    # handles spaces and apostrophes in customer install paths.
    $scriptLiteral = "'" + $PSCommandPath.Replace("'", "''") + "'"
    $directoryLiteral = "'" + $source.Directory.Replace("'", "''") + "'"
    $command = '& ' + $scriptLiteral + ' -Probe -SdkDirectory ' + $directoryLiteral
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    # Windows can have different 32/64-bit local execution policies. Carry the
    # caller's effective policy into the worker; Group Policy still takes precedence.
    $process = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', (Get-ExecutionPolicy), '-OutputFormat', 'Text', '-EncodedCommand', $encoded) -WorkingDirectory $output -RedirectStandardOutput $log -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        $process.WaitForExit()
        $status = 'TIMEOUT (worker stopped; Sage session cleanup could not be guaranteed)'
    }
    else {
        # Read RESULT too: script-policy/preflight failures may precede probe output.
        $passed = Select-String -LiteralPath $log -Pattern '^RESULT: PASS' -Quiet
        $status = if ($process.ExitCode -eq 0 -and $passed) { 'PASS' } else { 'FAIL - inspect log and stderr' }
    }
    $results += ($source.Name + ': ' + $status)
    if (Select-String -LiteralPath $log -Pattern '^SOURCE COMPARISON: INCONCLUSIVE' -Quiet) {
        $results += ($source.Name + ': source comparison INCONCLUSIVE - .NET redirected the API; see actual paths in log.')
    }
    $process.Dispose()
}
$summary = @(
    ('Sage SDK comparison v2 - ' + [DateTime]::UtcNow.ToString('o')),
    'Company names/locations are in bundled.txt and installed.txt; file and machine metadata are in inventory.txt.',
    'Fresh x86 processes; same working directory and inherited PATH.',
    'No company opened, approval requested, connector config read, or Rutter request sent.',
    'Uses empty SDK identity: tests discovery only, not real-company authorization.',
    'This is a standalone probe, not an exact reproduction of the connector EXE loader.',
    ''
) + $results
$summary | Set-Content -LiteralPath (Join-Path $output 'summary.txt') -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }
Write-Host ('Reports: ' + $output)
