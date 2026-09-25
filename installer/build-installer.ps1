# Builds dist\RAF.exe and compiles installer\RAF.iss into dist\RAF-Setup.exe.
# ASCII-only on purpose: Windows PowerShell 5.1 reads a BOM-less script in the system codepage.
#
# Usage:  .\installer\build-installer.ps1              (version taken from RAF.App.csproj)
#         .\installer\build-installer.ps1 -NoPublish   (reuse the existing dist\RAF.exe)

[CmdletBinding()]
param([switch] $NoPublish)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here

function Fail($msg) { Write-Host "`nINSTALLER FAILED: $msg" -ForegroundColor Red; exit 1 }

$csproj = Join-Path $root 'src\RAF.App\RAF.App.csproj'
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "No <Version> in $csproj" }

# ISCC is not on PATH after a winget install, and it lands under LOCALAPPDATA rather than Program Files.
$iscc = $null
foreach ($c in @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)) { if (Test-Path $c) { $iscc = $c; break } }
if (-not $iscc) { Fail "Inno Setup 6 not found. Install it once with: winget install --id JRSoftware.InnoSetup --source winget" }

$exe = Join-Path $root 'dist\RAF.exe'
if (-not $NoPublish) {
    Write-Host "--- Publishing RAF.exe $version ---" -ForegroundColor Cyan
    # A running RAF keeps the old exe locked, and publish then leaves it in place without an error.
    if (Get-Process RAF -ErrorAction SilentlyContinue) { Fail "RAF is running - close it first." }
    $before = if (Test-Path $exe) { (Get-Item $exe).LastWriteTimeUtc } else { [datetime]::MinValue }
    & dotnet publish (Join-Path $root 'src\RAF.App\RAF.App.csproj') -c Release -o (Join-Path $root 'dist') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }
    if (-not (Test-Path $exe) -or (Get-Item $exe).LastWriteTimeUtc -le $before) { Fail "dist\RAF.exe was not replaced" }
}
if (-not (Test-Path $exe)) { Fail "Missing dist\RAF.exe" }

# Inno Setup 6 reads a BOM-less file in the system codepage, which mangles every Hebrew string.
$iss = Join-Path $here 'RAF.iss'
$bytes = [System.IO.File]::ReadAllBytes($iss)
if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
    $text = [System.IO.File]::ReadAllText($iss, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($iss, $text, (New-Object System.Text.UTF8Encoding($true)))
}

Write-Host "--- Compiling installer $version ---" -ForegroundColor Cyan
# ISCC writes progress to stderr; judge it by exit code only.
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { & $iscc "/DAppVersion=$version" /Q $iss } finally { $ErrorActionPreference = $prev }
if ($LASTEXITCODE -ne 0) { Fail "ISCC exited with $LASTEXITCODE" }

$setup = Join-Path $root 'dist\RAF-Setup.exe'
Write-Host ("OK: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
