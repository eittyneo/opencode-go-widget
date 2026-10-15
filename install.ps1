# Installs the OpenCode Go desktop widget for the current user:
#   1. builds OpenCodeQuotaWidget.exe if it is missing
#   2. writes a plugin loader into the OpenCode config directory so the
#      plugin starts (and stops) together with OpenCode
#
# Nothing is installed system-wide. To uninstall, delete the loader file
# printed below and this repository folder.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 1. Build the widget if needed.
$exe = Join-Path $root "OpenCodeQuotaWidget.exe"
if (-not (Test-Path $exe)) {
    & (Join-Path $root "build.ps1")
}

# 2. Locate the OpenCode config directory (same rules OpenCode uses).
if ($env:OPENCODE_CONFIG_DIR) {
    $configDir = $env:OPENCODE_CONFIG_DIR
} elseif ($env:XDG_CONFIG_HOME) {
    $configDir = Join-Path $env:XDG_CONFIG_HOME "opencode"
} else {
    $configDir = Join-Path $env:USERPROFILE ".config\opencode"
}

# 3. Write the plugin loader stub into the auto-discovered plugins folder.
$pluginsDir = Join-Path $configDir "plugins"
New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
$stubPath = Join-Path $pluginsDir "opencode-go-widget.js"
$pluginPath = (Join-Path $root "plugin.js") -replace "\\", "/"
$pluginUrl = "file:///" + [System.Uri]::EscapeUriString($pluginPath)
Set-Content -Path $stubPath -Value ('export { default } from "' + $pluginUrl + '";') -Encoding UTF8

Write-Host ""
Write-Host "OpenCode Go widget installed:" -ForegroundColor Green
Write-Host "  widget exe:  $exe"
Write-Host "  plugin:      $stubPath"
Write-Host ""
Write-Host "Restart OpenCode. The floating widget opens automatically with the OpenCode desktop app."
Write-Host "While OpenCode is running you can reopen the widget anytime with the /widget command."
