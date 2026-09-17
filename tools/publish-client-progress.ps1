<#
.SYNOPSIS
  Publishes the CLIENT PROGRESS PAGE (task sheet) to its Hostinger site.
  Uploads ONLY the 4 progress page files. It never uploads the main project and never deletes remote files.

.DESCRIPTION
  Site   : https://colombowardrobecompany-build-tracker.kt-wedding.online
  FTP    : 147.93.101.68:21, FTPS (explicit TLS) with the Hostinger certificate public key pinned
  Folder : /public_html (the FTP account opens there directly)
  Source : D:\Projects\e com\client-progress\  (outside this repo)
  Login  : read from %USERPROFILE%\.spencer-denim\tracker-ftp-credential.xml
           (DPAPI-encrypted for the current Windows user). The password is never stored in the repo or docs.

  See docs/10-client-progress-publishing.md

.EXAMPLE
  pwsh tools/publish-client-progress.ps1              # validate, upload, verify
  pwsh tools/publish-client-progress.ps1 -DryRun      # validate only, no upload
  pwsh tools/publish-client-progress.ps1 -SetPassword # store or replace the FTP password (prompts, hidden)
  pwsh tools/publish-client-progress.ps1 -ShowPin     # show the server certificate + pin (after Hostinger renews it)
#>
[CmdletBinding()]
param(
  [switch]$DryRun,
  [switch]$SetPassword,
  [switch]$ShowPin
)

$ErrorActionPreference = "Stop"

# ---- Fixed configuration (not secret) ----
$FtpHost    = "147.93.101.68"
$FtpPort    = 21
$FtpUser    = "u765465138.colombowardrobecompany-build-tracker.kt-wedding.online"
$SiteUrl    = "https://colombowardrobecompany-build-tracker.kt-wedding.online"
$SourceDir  = "D:\Projects\e com\client-progress"
$AllowList  = @("index.html", "styles.css", "app.js", "progress-data.js")
# Uploaded too, but not size-verified: FTP LIST hides dot-files. Verified through response headers instead.
$ExtraFiles = @(".htaccess")
# SHA-256 public-key pins for Hostinger's FTP certificate. Several are allowed because Hostinger
# rotates between certificates (Let's Encrypt ones change every few months). curl accepts a
# semicolon-separated list and connects only if the server key matches one of them.
#   1) CN=*.hstgr.io      Let's Encrypt, seen 2026-09-18, expires 2026-11-11
#   2) CN=hostinger.com   Sectigo,       seen 2026-09-17, expires 2027-01-17
# Refresh with:  pwsh tools/publish-client-progress.ps1 -ShowPin     (see docs/10 §4)
$PinnedKey  = "sha256//iD1qTPTlmilSMjmnGtuhIjGlRZY9GF4YIhDr8P0WXNw=;sha256//rRBXAmuSvqI5e99CZPWQDXLPWIlTIvu77x/aZc349JQ="
$CredFile   = Join-Path $env:USERPROFILE ".spencer-denim\tracker-ftp-credential.xml"

function Save-Password {
  New-Item -ItemType Directory -Force -Path (Split-Path $CredFile) | Out-Null
  $sec = Read-Host -AsSecureString "FTP password for $FtpUser"
  New-Object System.Management.Automation.PSCredential($FtpUser, $sec) | Export-Clixml -Path $CredFile
  Write-Host "Saved (encrypted for $env:USERNAME only): $CredFile"
}

function Show-Pin {
  $script:info = $null
  [System.Net.ServicePointManager]::ServerCertificateValidationCallback = [System.Net.Security.RemoteCertificateValidationCallback]{
    param($s, $cert, $chain, $errors)
    $c2 = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cert)
    $spki = $c2.PublicKey.ExportSubjectPublicKeyInfo()
    $pin = "sha256//" + [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::HashData($spki))
    $script:info = "Subject : $($c2.Subject)`nIssuer  : $($c2.Issuer)`nExpires : $($c2.NotAfter)`nErrors  : $errors`nPin     : $pin"
    return $false   # never log in during this check
  }
  try {
    $req = [System.Net.FtpWebRequest]::Create("ftp://${FtpHost}:$FtpPort/")
    $req.Method = [System.Net.WebRequestMethods+Ftp]::PrintWorkingDirectory
    $req.Credentials = New-Object System.Net.NetworkCredential("probe", "x")
    $req.EnableSsl = $true; $req.Timeout = 20000
    $req.GetResponse() | Out-Null
  } catch { }
  finally { [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null }
  Write-Host $script:info
  Write-Host "Add this pin to `$PinnedKey (semicolon-separated) only if ALL of these hold:"
  Write-Host "  - Subject is a Hostinger name: CN=hostinger.com or CN=*.hstgr.io"
  Write-Host "  - Issuer is a public CA (e.g. Sectigo, Let's Encrypt)"
  Write-Host "  - The only error is RemoteCertificateNameMismatch (we connect by IP, so the name cannot match)"
  Write-Host "Keep at most 3 pins; drop expired ones."
}

function Invoke-Curl([string[]]$CurlArgs) {
  $nc = (Import-Clixml $CredFile).GetNetworkCredential()
  if ($nc.UserName -ne $FtpUser) { throw "Stored credential is for '$($nc.UserName)', expected '$FtpUser'." }
  # Credentials go through stdin (-K -), so they never appear on the command line
  $cfg = "user = `"$($nc.UserName):$($nc.Password)`""
  $out = $cfg | & curl.exe -K - --ssl-reqd --insecure --pinnedpubkey $PinnedKey --ftp-pasv --connect-timeout 20 --max-time 120 -s -S @CurlArgs 2>&1
  return @{ Output = ($out | Out-String); Exit = $LASTEXITCODE }
}

if ($SetPassword) { Save-Password; return }
if ($ShowPin)     { Show-Pin; return }

Write-Host "== Client progress page publish ==" -ForegroundColor Cyan

# 1) Validate the source folder: exactly the allow-listed files
if (-not (Test-Path $SourceDir)) { throw "Source folder not found: $SourceDir" }
$present = Get-ChildItem $SourceDir -File | Select-Object -ExpandProperty Name
$missing = $AllowList | Where-Object { $_ -notin $present }
$extra   = $present   | Where-Object { $_ -notin $AllowList -and $_ -notin $ExtraFiles }
if ($missing) { throw "Missing required files: $($missing -join ', ')" }
if ($extra)   { Write-Warning "Ignoring files that are not on the allow-list (they will NOT be uploaded): $($extra -join ', ')" }
if (Get-ChildItem $SourceDir -Directory) { Write-Warning "Sub-folders in $SourceDir are ignored." }

# 2) Validate JavaScript syntax and data values
foreach ($js in @("app.js", "progress-data.js")) {
  & node --check (Join-Path $SourceDir $js)
  if ($LASTEXITCODE -ne 0) { throw "Syntax error in $js. Fix it before publishing (see docs/07)." }
}
$summary = & node -e @"
const fs=require('fs'),vm=require('vm');const c=vm.createContext({});
vm.runInContext(fs.readFileSync(String.raw``$SourceDir\progress-data.js``,'utf8')+';this.d=progressData;',c);
const d=c.d,f=d.groups.flatMap(g=>g.features),n=k=>f.filter(x=>x.status===k).length;
const pct=Math.round(f.reduce((a,x)=>a+(x.status==='done'?100:x.status==='in-progress'?(x.progress||0):0),0)/f.length);
if(!/^\d{4}-\d{2}-\d{2}$/.test(d.lastUpdated)) { console.error('lastUpdated must be YYYY-MM-DD'); process.exit(2); }
console.log('lastUpdated '+d.lastUpdated+' | features '+f.length+' | overall '+pct+'% | done '+n('done')+', in progress '+n('in-progress')+', planned '+n('planned'));
"@
if ($LASTEXITCODE -ne 0) { throw "progress-data.js failed validation." }
Write-Host "Data OK: $summary"
$lastUpdated = ($summary -split ' ')[1]

if ($DryRun) { Write-Host "Dry run: nothing uploaded." -ForegroundColor Yellow; return }

# 3) Credential present?
if (-not (Test-Path $CredFile)) { Write-Host "No stored FTP password yet."; Save-Password }

# 4) Upload the allow-listed files only (overwrite, never delete)
foreach ($name in $AllowList) {
  $local = Join-Path $SourceDir $name
  $r = Invoke-Curl @("-T", $local, "ftp://${FtpHost}:$FtpPort/$name")
  if ($r.Exit -ne 0) { throw "Upload failed for $name (curl exit $($r.Exit)): $($r.Output)" }
  Write-Host ("Uploaded  {0,-18} {1,7:N0} bytes" -f $name, (Get-Item $local).Length)
}

# 4b) Upload the extra files (cache rules). Not size-verified: FTP LIST hides dot-files.
foreach ($name in $ExtraFiles) {
  $local = Join-Path $SourceDir $name
  if (-not (Test-Path -LiteralPath $local)) { Write-Warning "$name not found in $SourceDir - skipped."; continue }
  $r = Invoke-Curl @("-T", $local, "ftp://${FtpHost}:$FtpPort/$name")
  if ($r.Exit -ne 0) { throw "Upload failed for $name (curl exit $($r.Exit)): $($r.Output)" }
  Write-Host ("Uploaded  {0,-18} {1,7:N0} bytes" -f $name, (Get-Item $local).Length)
}

# 5) Verify remote sizes match
$list = Invoke-Curl @("ftp://${FtpHost}:$FtpPort/")
if ($list.Exit -ne 0) { throw "Could not list remote folder: $($list.Output)" }
foreach ($name in $AllowList) {
  $localSize = (Get-Item (Join-Path $SourceDir $name)).Length
  $line = ($list.Output -split "`n") | Where-Object { $_ -match "\s$([regex]::Escape($name))\s*$" }
  $remoteSize = if ($line) { [long](($line -split '\s+')[4]) } else { -1 }
  if ($remoteSize -ne $localSize) { throw "Size mismatch for ${name}: local $localSize, remote $remoteSize" }
}
Write-Host "Remote file sizes verified."

# 6) Verify the live site serves the new data
try {
  $live = Invoke-WebRequest -Uri "$SiteUrl/progress-data.js?v=$(Get-Date -Format yyyyMMddHHmmss)" -UseBasicParsing -TimeoutSec 30 -Headers @{ "Cache-Control" = "no-cache" }
  if ($live.Content -match "lastUpdated:\s*`"$lastUpdated`"") {
    Write-Host "Live site shows lastUpdated $lastUpdated" -ForegroundColor Green
  } else {
    Write-Warning "Upload succeeded, but the live site does not show lastUpdated $lastUpdated yet (cache). Re-check in a few minutes."
  }
  # Cache check: without no-cache headers the client keeps seeing an old page for up to a week.
  # The new rules take a few seconds to apply, so retry before warning.
  $cacheValue = ""
  foreach ($attempt in 1..4) {
    $cacheValue = ((& curl.exe -sSI "$SiteUrl/progress-data.js" 2>&1 | Select-String -Pattern "^cache-control:") | ForEach-Object { $_.Line.Trim() }) -join " "
    if ($cacheValue -match "no-cache|no-store|max-age=0") { break }
    if ($attempt -lt 4) { Start-Sleep -Seconds 5 }
  }
  if ($cacheValue -match "no-cache|no-store|max-age=0") {
    Write-Host "Cache headers OK: $cacheValue" -ForegroundColor Green
  } else {
    Write-Warning "The server still caches the page ('$cacheValue'). Check that .htaccess uploaded, or purge the cache in hPanel; visitors may see an old version."
  }
} catch {
  Write-Warning "Upload succeeded, but the live check failed: $($_.Exception.Message)"
}
Write-Host "Published: $SiteUrl" -ForegroundColor Green
