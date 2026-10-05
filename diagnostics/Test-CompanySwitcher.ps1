# Run with 32-bit PowerShell -STA. Uses fake credentials and an isolated config
# directory; never opens Sage, touches the live config, or consumes Rutter jobs.
param([string]$ConnectorPath = 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe', [int]$Port = 18089)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 4) { throw 'Run with SysWOW64 PowerShell -STA.' }
$assembly = [Reflection.Assembly]::LoadFrom($ConnectorPath)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$configType = $assembly.GetType('Sage50Connector.Helpers.ConnectorConfig')
$registryType = $assembly.GetType('Sage50Connector.Helpers.ConnectionRegistry')
$probeType = $assembly.GetType('Sage50Connector.Helpers.ConnectionProbe')
$root = Join-Path $env:TEMP ('sage-switch-test-' + [guid]::NewGuid())
New-Item -ItemType Directory $root | Out-Null
# Redirect every config/log path in this test process before any connector call.
$configType.GetField('ConfigDirectory').SetValue($null, $root)
foreach ($field in @('ConfigFilePath','LegacyConfigFilePath','LogFilePath','LegacyLogFilePath')) {
    $configType.GetField($field).SetValue($null, (Join-Path $root ($field + '.json')))
}
$registryType.GetField('FilePath', $flags).SetValue($null, (Join-Path $root 'connections.json'))
function Assert($condition, $message) { if (!$condition) { throw "FAIL: $message" }; Write-Output "PASS: $message" }
$valid = '{"job_id":"mock-job","type":"LIST_FETCH","platform_entity":"VENDORS","parameters":{"limit":50}}'
$auth = '{"code":"INVALID_REQUEST","message":"Invalid access token/connectionId pair. Please check the credentials"}'
$cases = @(
    @{code=200; body=$valid; expected='Alive'; name='valid mock'},
    @{code=401; body=$auth; expected='Disconnected'; name='revoked token'},
    @{code=200; body=$auth; expected='Disconnected'; name='auth rejection with rewritten status'},
    @{code=404; body='<html>ERR_NGROK_3200: tunnel offline</html>'; expected='Unreachable'; name='offline tunnel'},
    @{code=404; body=$auth; expected='Disconnected'; name='Rutter auth envelope on 404'},
    @{code=200; body='<html>gateway</html>'; expected='Unreachable'; name='HTML success'},
    @{code=200; body='{}'; expected='Unreachable'; name='empty success'},
    @{code=200; body='not json'; expected='Unreachable'; name='malformed success'},
    @{code=200; body='{"type":"NOOP"}'; expected='Unreachable'; name='normal poll is not the probe contract'},
    @{code=200; body='{"code":{},"message":[]}'; expected='Unreachable'; name='malformed error fields'},
    @{code=200; body='{"type":"LIST_FETCH","job_id":"mock-job","platform_entity":"VENDORS"}'; expected='Unreachable'; name='incomplete mock'},
    @{code=500; body=$valid; expected='Unreachable'; name='server failure'},
    @{code=403; body='{}'; expected='Disconnected'; name='forbidden'},
    @{code=410; body='{}'; expected='Disconnected'; name='gone'},
    @{code=302; body=$valid; expected='Unreachable'; name='redirect is not accepted'}
)
$transcript = Join-Path $root 'requests.jsonl'
$server = Start-Job -ArgumentList $Port, $cases, $transcript -ScriptBlock {
    param($port, $cases, $path)
    $listener = New-Object Net.HttpListener
    $listener.Prefixes.Add("http://localhost:$port/"); $listener.Start()
    try {
        foreach ($case in $cases) {
            $waiting = $listener.GetContextAsync()
            if (!$waiting.Wait(30000)) { throw 'No probe request within 30 seconds.' }
            $context = $waiting.GetAwaiter().GetResult()
            $reader = New-Object IO.StreamReader($context.Request.InputStream)
            $body = $reader.ReadToEnd(); $reader.Dispose()
            @{body=$body; version=$context.Request.Headers['X-Rutter-Version']; fakeAuth=($context.Request.Headers['Authorization'] -eq 'Bearer iat_fake_probe'); path=$context.Request.Url.AbsolutePath; method=$context.Request.HttpMethod} | ConvertTo-Json -Compress | Add-Content $path
            $context.Response.StatusCode = $case.code
            if ($case.code -eq 302) { $context.Response.RedirectLocation = "http://localhost:$port/redirected" }
            $bytes = [Text.Encoding]::UTF8.GetBytes($case.body)
            $context.Response.OutputStream.Write($bytes,0,$bytes.Length); $context.Response.Close()
        }
    } finally { $listener.Stop() }
}
$form = $null
try {
    Start-Sleep 2
    foreach ($case in $cases) {
        $task = $probeType.GetMethod('ProbeAsync').Invoke($null, @("http://localhost:$Port",'probe-test','iat_fake_probe'))
        $result = $task.GetAwaiter().GetResult()
        Assert ($result.Status -eq $case.expected -and $result.StatusCode -eq $case.code) $case.name
    }
    Wait-Job $server -Timeout 5 | Out-Null
    if ($server.State -ne 'Completed') { throw 'Probe fixture did not finish.' }
    Receive-Job $server -ErrorAction Stop | Out-Null
    $requests = @(Get-Content $transcript | ForEach-Object { $_ | ConvertFrom-Json })
    Assert ($requests.Count -eq $cases.Count) 'exactly one request per probe (no redirects or retries)'
    foreach ($request in $requests) {
        $body = $request.body | ConvertFrom-Json
        Assert ($request.fakeAuth -and $request.version -eq '2024-04-30' -and $request.method -eq 'POST' -and $request.path -eq '/versioned/ingest' -and $body.mock -eq 'LIST_FETCH' -and $body.connection.id -eq 'probe-test' -and !$body.type) 'side-effect-free authenticated mock request'
    }
    # Create a real StatusForm backed entirely by fake sandbox rows. Selecting a
    # disconnected row must return immediately, rather than show a switch modal.
    $configType.GetMethod('SaveAndRegister').Invoke($null,@('A active','iat_fake_active','active-test','http://localhost', [guid]::NewGuid().ToString(),'active-test')) | Out-Null
    $storedType = $assembly.GetType('Sage50Connector.Helpers.StoredConnection')
    $removed = [Activator]::CreateInstance($storedType,$true)
    $removed.CompanyName='B disconnected'; $removed.CompanyGuid=[guid]::NewGuid().ToString()
    $removed.ConnectionId='removed-test'; $removed.AccessKey='iat_fake_removed'; $removed.ApiBaseUrl='http://localhost'
    $registryType.GetMethod('Upsert').Invoke($null,@($removed,$false)) | Out-Null
    $registryType.GetMethod('RecordProbe').Invoke($null,@('removed-test','Disconnected')) | Out-Null
    $before = [IO.File]::ReadAllText($configType.GetField('ConfigFilePath').GetValue($null))
    $form = [Activator]::CreateInstance($assembly.GetType('Sage50Connector.Ui.StatusForm'))
    $combo = $form.GetType().GetField('companyCombo',$flags).GetValue($form)
    $remove = $form.GetType().GetField('removeCompanyButton',$flags).GetValue($form)
    Assert ($combo.SelectedIndex -eq 0 -and !$remove.Enabled) 'active company cannot be removed'
    $combo.SelectedIndex = 1
    Assert ($combo.SelectedIndex -eq 1 -and $remove.Enabled) 'disconnected row can stay selected for removal without a switch'
    Assert ([IO.File]::ReadAllText($configType.GetField('ConfigFilePath').GetValue($null)) -eq $before) 'selecting disconnected row preserves active config'
    Assert ($registryType.GetMethod('Remove').Invoke($null,@('removed-test'))) 'disconnected row can be removed'
    Assert (!$registryType.GetMethod('Remove').Invoke($null,@('active-test'))) 'registry protects active company'
    $form.GetType().GetMethod('RefreshCompanies',$flags).Invoke($form,@()) | Out-Null
    Assert ($combo.Items.Count -eq 1 -and $combo.SelectedIndex -eq 0 -and !$remove.Enabled) 'removal preserves active selection and disables Remove'
    Assert ([IO.File]::ReadAllText($configType.GetField('ConfigFilePath').GetValue($null)) -eq $before) 'removal preserves active config'
    Write-Output 'COMPANY SWITCHER REGRESSION OK'
} finally {
    if ($form) { $form.Dispose() }
    Stop-Job $server -ErrorAction SilentlyContinue
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
