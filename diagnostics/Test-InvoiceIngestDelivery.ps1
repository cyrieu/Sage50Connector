# Real connector handler and HTTP delivery, synthetic SDK-free reader.
# Run with 32-bit PowerShell against the diagnostic executable.
param([string]$ConnectorPath = 'C:\src\InvoiceWindowDiagnosticFinal\bin\Release\Sage50Connector.exe', [int]$Port=18088)
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom($ConnectorPath)
$flags=[Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
$program=$assembly.GetType('Sage50Connector.Program')
$configType=$assembly.GetType('Sage50Connector.Helpers.ConnectorConfig')
$config=[Activator]::CreateInstance($configType,$true)
$configType.GetProperty('ApiBaseUrl').GetSetMethod($true).Invoke($config,@("http://localhost:$Port")) | Out-Null
$program.GetField('Config',$flags).SetValue($null,$config)
$program.GetField('ConnectionId',$flags).SetValue($null,'invoice-delivery-test')
$cache=$assembly.GetType('Sage50Connector.Helpers.InvoiceFetchCache')
$tests=$assembly.GetType('Sage50Connector.Diagnostics.InvoiceWindowReaderTests')
function Seed($id) {
    $reader=$tests.GetMethod('CreateDeliveryReader',$flags).Invoke($null,@())
    $cache.GetMethod('Put',$flags).Invoke($null,@($id,$reader)) | Out-Null
}
function Has-Reader($id) {
    $arguments=[object[]]@($id,$null)
    return $cache.GetMethod('TryGet',$flags).Invoke($null,$arguments)
}
function Invoke-Async($name,[object[]]$arguments) {
    for($i=0;$i -lt $arguments.Length;$i++) { if($null -ne $arguments[$i]) { $arguments[$i]=$arguments[$i].PSObject.BaseObject } }
    $task=$program.GetMethod($name,$flags).Invoke($null,$arguments)
    return $task.GetAwaiter().GetResult()
}
function Fetch($id) {
    $job=[Activator]::CreateInstance($assembly.GetType('Sage50Connector.ResponseObject'),$true)
    $job.job_id=$id; $job.type='LIST_FETCH'; $job.platform_entity='INVOICES'
    $job.parameters=[Activator]::CreateInstance($assembly.GetType('Sage50Connector.Parameters'),$true)
    $job.parameters.limit=1
    return $job
}
function Assert($value,$message) { if(!$value) { throw "FAIL: $message" }; Write-Output "PASS: $message" }
$transcript=Join-Path $env:TEMP ('invoice-delivery-'+[guid]::NewGuid()+'.jsonl')
$server=Start-Job -ArgumentList $Port,$transcript -ScriptBlock {
    param($port,$path)
    $listener=New-Object Net.HttpListener; $listener.Prefixes.Add("http://localhost:$port/"); $listener.Start()
    try {
        foreach($code in @(502,200,400,200,400,200,200)) {
            $context=$listener.GetContext(); $reader=New-Object IO.StreamReader($context.Request.InputStream)
            $raw=$reader.ReadToEnd(); $reader.Dispose(); $body=$raw | ConvertFrom-Json
            @{code=$code; body=$raw} | ConvertTo-Json -Compress | Add-Content $path
            $response='{"type":"NOOP"}'
            if($code -eq 200 -and $body.next_cursor) {
                $response=@{job_id=$body.job_id; type='LIST_FETCH'; platform_entity='INVOICES'; parameters=@{limit=1; cursor=$body.next_cursor}} | ConvertTo-Json -Compress
            }
            $context.Response.StatusCode=$code; $bytes=[Text.Encoding]::UTF8.GetBytes($response)
            $context.Response.OutputStream.Write($bytes,0,$bytes.Length); $context.Response.Close()
        }
    } finally { $listener.Stop() }
}
try {
    Start-Sleep 2
    Seed 'pages'
    Invoke-Async 'HandleListFetchJob' @((Fetch 'pages'),'iat_fake_local','unused')
    $next=Invoke-Async 'GetNextJobAsync' @('iat_fake_local',[Threading.CancellationToken]::None)
    Assert ($next.parameters.cursor.StartsWith('invoice-v1:')) 'opaque cursor carried through real report response'
    Assert (Has-Reader 'pages') 'first page reader retained after acknowledgement'
    try { Invoke-Async 'HandleListFetchJob' @($next,'iat_fake_local','unused'); throw 'Unexpected success' }
    catch { Assert ($_.Exception.ToString().Contains('ReportDeliveryException')) 'rejected final report propagates delivery failure'
    }
    Assert (Has-Reader 'pages') 'final reader held until upload accepted'
    $noop=Invoke-Async 'GetNextJobAsync' @('iat_fake_local',[Threading.CancellationToken]::None)
    Assert ($noop.type -eq 'NOOP' -and !(Has-Reader 'pages')) 'replayed final report cleans up only after acknowledgement'
    Seed 'outage'
    try { Invoke-Async 'HandleListFetchJob' @((Fetch 'outage'),'iat_fake_local','unused'); throw 'Unexpected success' }
    catch { Assert ($_.Exception.ToString().Contains('ReportDeliveryException')) 'rejected first page retained for recovery' }
    $sdk=$assembly.GetType('Sage50Connector.Helpers.Sage50Connector')
    $instance=$sdk.GetProperty('Instance').GetValue($null,$null)
    $sdk.GetMethod('Shutdown').Invoke($instance,@()) | Out-Null
    Assert (!(Has-Reader 'outage')) 'actual Sage shutdown invalidates the invoice reader'
    $next=Invoke-Async 'GetNextJobAsync' @('iat_fake_local',[Threading.CancellationToken]::None)
    Assert ($next.parameters.cursor.StartsWith('invoice-v1:')) 'late acknowledgement survives discarded Sage reader'
    Invoke-Async 'HandleListFetchJob' @($next,'iat_fake_local','unused')
    $server | Wait-Job -Timeout 15 | Out-Null
    $requests=@(Get-Content $transcript | ForEach-Object { $_ | ConvertFrom-Json })
    Assert ($requests.Count -eq 7) 'no extra polls or misleading job failures'
    Assert ($requests[0].body -ceq $requests[1].body -and $requests[2].body -ceq $requests[3].body -and $requests[4].body -ceq $requests[5].body) 'transient retry and later replays preserve exact report bytes'
    $reset=$requests[6].body | ConvertFrom-Json
    Assert ($reset.restart_from_beginning -eq $true) 'lost reader requests server cursor reset before another SDK read'
    Write-Output 'INVOICE INGEST DELIVERY TESTS PASSED'
} finally { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue; Remove-Item $transcript -ErrorAction SilentlyContinue }
