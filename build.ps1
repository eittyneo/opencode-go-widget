# Builds OpenCodeQuotaWidget.exe with the .NET Framework compiler that ships
# with Windows. No SDK, no NuGet, no admin rights required.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    throw "Could not find csc.exe (.NET Framework 4.x is required and normally ships with Windows)."
}

$out = Join-Path $root "OpenCodeQuotaWidget.exe"
$src = Join-Path $root "OpenCodeQuotaWidget.cs"

& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$out" "$src"
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Host "Built: $out" -ForegroundColor Green
