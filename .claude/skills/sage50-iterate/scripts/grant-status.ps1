# What Sage has authorized, and whether the binary on disk is covered.
#
# Sage records approved applications in APIACCSS.DAT inside each company
# directory, one entry per application, keyed by MD5 of the executable. So a
# rebuild with no source change stays authorized (the build is deterministic and
# the bytes are identical), while any real code change is a new identity.
#
# Note the file records a *request* as well as a grant, so the hash being present
# does not by itself prove the grant was approved — if the connector still says
# Pending while the hash is listed, the dialog has not been confirmed yet.
$exe = 'C:\src\Sage50Connector\bin\Release\Sage50Connector.exe'
if (-not (Test-Path $exe)) { Write-Output "no exe at $exe"; exit 1 }

$md5 = [Security.Cryptography.MD5]::Create()
$cur = [Convert]::ToBase64String($md5.ComputeHash([IO.File]::ReadAllBytes($exe)))
Write-Output ('exe   ' + $exe)
Write-Output ('mtime ' + (Get-Item $exe).LastWriteTime)
Write-Output ('MD5   ' + $cur)
Write-Output ''

function Strings($path) {
  $bytes = [IO.File]::ReadAllBytes($path)
  $sb = New-Object System.Text.StringBuilder
  $out = New-Object System.Collections.ArrayList
  foreach ($b in $bytes) {
    if ($b -ge 32 -and $b -lt 127) { [void]$sb.Append([char]$b) }
    else { if ($sb.Length -ge 4) { [void]$out.Add($sb.ToString()) }; [void]$sb.Clear() }
  }
  if ($sb.Length -ge 4) { [void]$out.Add($sb.ToString()) }
  return $out
}

Get-ChildItem 'C:\Sage\Peachtree\Company' -Recurse -Filter 'APIACCSS.DAT' -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\Archives\\' } |
  ForEach-Object {
    $s = Strings $_.FullName
    $entries = ($s | Where-Object { $_ -eq 'Sage50Connector.exe' }).Count
    Write-Output ($_.FullName)
    Write-Output ('   connector entries: ' + $entries)
    # Sage writes an entry when access is *requested*, not only when it is
    # granted, so this answers "has this binary ever asked?" and NOT "is it
    # authorized?". Only a run that fetches data proves the grant.
    Write-Output ('   current exe hash recorded (requested or granted): ' + ($s -contains $cur))
    Write-Output ('   last written: ' + $_.LastWriteTime)
  }
