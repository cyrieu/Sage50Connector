# Run under 32-bit PowerShell on the Sage lab VM. No Sage reads or writes:
# seeded snapshots exercise the actual connector handlers against loopback HTTP.
param([string]$ConnectorPath = 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe', [int]$Port = 18087)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom($ConnectorPath)
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
$program = $assembly.GetType('Sage50Connector.Program')
$configType = $assembly.GetType('Sage50Connector.Helpers.ConnectorConfig')
$config = [Activator]::CreateInstance($configType, $true)
$configType.GetProperty('ApiBaseUrl').GetSetMethod($true).Invoke($config, @("http://localhost:$Port")) | Out-Null
$program.GetField('Config', $flags).SetValue($null, $config)
$program.GetField('ConnectionId', $flags).SetValue($null, 'delivery-test')
$cache = $assembly.GetType('Sage50Connector.Helpers.JobFetchCache')
$jobType = $assembly.GetType('Sage50Connector.ResponseObject')
$parametersType = $assembly.GetType('Sage50Connector.Parameters')
function Invoke-Async($name, [object[]]$arguments) {
    $task = $program.GetMethod($name, $flags).Invoke($null, $arguments)
    $task.GetAwaiter().GetResult()
}
function Assert($condition, $message) { if (!$condition) { throw "FAIL: $message" }; Write-Output "PASS: $message" }
function New-Fetch($id, $cursor) {
    $job = [Activator]::CreateInstance($jobType, $true)
    $job.job_id = $id; $job.type = 'LIST_FETCH'; $job.platform_entity = 'BILLS'
    $job.parameters = [Activator]::CreateInstance($parametersType, $true)
    $job.parameters.limit = 1; $job.parameters.cursor = $cursor
    return $job
}
function Seed($id) {
    $records = New-Object 'System.Collections.Generic.List[object]'
    $records.Add([pscustomobject]@{id='a'}); $records.Add([pscustomobject]@{id='b'})
    $cache.GetMethod('Put').Invoke($null, @($id, 'BILLS', $records)) | Out-Null
}
function Has-Snapshot($id) {
    $arguments = [object[]]@($id, 'BILLS', $null)
    return $cache.GetMethod('TryGet').Invoke($null, $arguments)
}
$transcript = Join-Path $env:TEMP ('sage-delivery-' + [guid]::NewGuid() + '.jsonl')
# A fixed response plan detects unintended polls/error reports as well as lost jobs.
$plan = @(
    @{code=502; body='{}'}, @{code=503; body='{}'}, @{code=504; body='{}'}, @{code=200; body='{"type":"NOOP"}'},
    @{code=400; body='{}'}, @{code=200; body='{"job_id":"paged","type":"LIST_FETCH","platform_entity":"BILLS","parameters":{"limit":1,"cursor":"a"}}'},
    @{code=400; body='{}'}, @{code=200; body='{"job_id":"next-je","type":"LIST_FETCH","platform_entity":"JOURNAL_ENTRIES","parameters":{"limit":50}}'},
    @{code=400; body='{}'}, @{code=200; body='{"type":"NOOP"}'}
)
1..8 | ForEach-Object { $plan += @{code=503; body='{}'} }
$plan += @{code=200; body='{"job_id":"next-invoice","type":"LIST_FETCH","platform_entity":"INVOICES","parameters":{"limit":50}}'}
$server = Start-Job -ArgumentList $Port, $plan, $transcript -ScriptBlock {
    param($port, $plan, $path)
    $listener = New-Object Net.HttpListener
    $listener.Prefixes.Add("http://localhost:$port/"); $listener.Start()
    try {
        foreach ($response in $plan) {
            $context = $listener.GetContext()
            $reader = New-Object IO.StreamReader($context.Request.InputStream)
            $body = $reader.ReadToEnd(); $reader.Dispose()
            @{body=$body; code=$response.code} | ConvertTo-Json -Compress | Add-Content $path
            $context.Response.StatusCode = $response.code
            $bytes = [Text.Encoding]::UTF8.GetBytes($response.body)
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length); $context.Response.Close()
        }
    } finally { $listener.Stop() }
}
try {
    Start-Sleep 2
    Invoke-Async 'PostToRutterAsync' @('{"test":"transient"}', 'iat_fake_local')
    $noop = Invoke-Async 'GetNextJobAsync' @('iat_fake_local', [Threading.CancellationToken]::None)
    Assert ($noop.type -eq 'NOOP') 'transient retries consume the report response without another poll'
    Seed 'paged'
    try { Invoke-Async 'HandleListFetchJob' @((New-Fetch 'paged' $null), 'iat_fake_local', 'unused'); throw 'Unexpected success' }
    catch { Assert ($_.Exception.ToString().Contains('ReportDeliveryException')) 'HTTP 400 is a delivery failure, not success or a Sage error report' }
    Assert (Has-Snapshot 'paged') 'unacknowledged first page retains snapshot'
    $next = Invoke-Async 'GetNextJobAsync' @('iat_fake_local', [Threading.CancellationToken]::None)
    Assert ($next.job_id -eq 'paged' -and $next.parameters.cursor -eq 'a') 'recovery consumes the acknowledged next-page job'
    Assert (Has-Snapshot 'paged') 'acknowledged nonfinal page retains snapshot'
    try { Invoke-Async 'HandleListFetchJob' @($next, 'iat_fake_local', 'unused'); throw 'Unexpected success' }
    catch { Assert ($_.Exception.ToString().Contains('ReportDeliveryException')) 'rejected final page propagates delivery failure' }
    Assert (Has-Snapshot 'paged') 'rejected final page does not discard snapshot'
    $next = Invoke-Async 'GetNextJobAsync' @('iat_fake_local', [Threading.CancellationToken]::None)
    Assert ($next.job_id -eq 'next-je') 'next entity claimed by report response is consumed'
    Assert (!(Has-Snapshot 'paged')) 'final-page cleanup occurs after replay acknowledgment'
    try { Invoke-Async 'ReportJobError' @((New-Fetch 'error-job' $null), 'iat_fake_local', (New-Object Exception 'Sage read failure')); throw 'Unexpected success' }
    catch { Assert ($_.Exception.ToString().Contains('ReportDeliveryException')) 'failed error-report delivery propagates safely' }
    $noop = Invoke-Async 'GetNextJobAsync' @('iat_fake_local', [Threading.CancellationToken]::None)
    Assert ($noop.type -eq 'NOOP') 'error report is replayed and its response consumed'
    Seed 'exhaust'
    try { Invoke-Async 'HandleListFetchJob' @((New-Fetch 'exhaust' 'a'), 'iat_fake_local', 'unused'); throw 'Unexpected success' }
    catch { Assert ($_.Exception.ToString().Contains('ReportDeliveryException')) 'eight HTTP 503 attempts fail rather than return success' }
    Assert (Has-Snapshot 'exhaust') 'retry exhaustion retains final-page snapshot'
    $next = Invoke-Async 'GetNextJobAsync' @('iat_fake_local', [Threading.CancellationToken]::None)
    Assert ($next.job_id -eq 'next-invoice' -and !(Has-Snapshot 'exhaust')) 'post-exhaustion recovery acknowledges and cleans up final page'
    $requests = @(Get-Content $transcript | ForEach-Object { $_ | ConvertFrom-Json })
    Assert ($requests.Count -eq 19) 'no extra polls or misleading error reports were sent'
    foreach ($range in @(@(0,1,2,3), @(4,5), @(6,7), @(8,9), @(10,11,12,13,14,15,16,17,18))) {
        $original = $requests[$range[0]].body
        foreach ($index in $range) { if ($requests[$index].body -cne $original) { throw 'Report changed during replay' } }
    }
    Assert $true 'all retries and recovery replays send byte-identical report bodies'
    $program.GetField('nextReportedJob', $flags).SetValue($null, $next)
    $program.GetMethod('ClearCachedCompanyState', $flags).Invoke($null, @()) | Out-Null
    Assert ($null -eq $program.GetField('nextReportedJob', $flags).GetValue($null) -and $null -eq $program.GetField('pendingReport', $flags).GetValue($null)) 'company switch clears retained report and returned job'
    Write-Output 'INGEST DELIVERY TESTS PASSED'
} finally {
    Stop-Job $server -ErrorAction SilentlyContinue
    Receive-Job $server -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item $transcript -ErrorAction SilentlyContinue
    $cache.GetMethod('Clear').Invoke($null, @()) | Out-Null
}
