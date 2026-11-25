# Builds the widget with the C# compiler that ships with Windows and installs it to %LOCALAPPDATA%\TodayCalendar.
#   .\build.ps1              build and install
#   .\build.ps1 -NoInstall   build to bin\ only
#   .\build.ps1 -Snapshots   also render docs\*.png from sample data
param([switch]$NoInstall, [switch]$Snapshots)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $src 'bin'
$dest = Join-Path $env:LOCALAPPDATA 'TodayCalendar'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll',
        'System.Web.Extensions.dll', 'System.Security.dll' | ForEach-Object { "/r:$fw\$_" }
# The app version comes from VERSION; release tags must match it (BWUI DESIGN.md §6.10).
$version = (Get-Content "$src\VERSION").Trim()
Set-Content "$out\AppInfo.g.cs" "namespace Bwui { public static class AppInfo { public const string Version = `"$version`"; } }" -Encoding utf8

# The app's Google OAuth client (Desktop app type), built in so users only sign in. It never goes into the
# repository: locally it comes from oauth-client.json next to this script (git-ignored), in CI from the
# GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET secrets. Without either, the build asks for a client file at runtime.
$clientId = $env:GOOGLE_CLIENT_ID; $clientSecret = $env:GOOGLE_CLIENT_SECRET
if (-not $clientId -and (Test-Path "$src\oauth-client.json")) {
    $j = Get-Content "$src\oauth-client.json" -Raw | ConvertFrom-Json
    $o = if ($j.installed) { $j.installed } else { $j }
    $clientId = $o.client_id; $clientSecret = $o.client_secret
}
if (-not $clientId) { Write-Warning 'No Google OAuth client: this build will ask for a client file.' }
Set-Content "$out\ClientInfo.g.cs" ('namespace TodayCalendar { static class BuiltInClient { public const string Id = "' + $clientId + '"; public const string Secret = "' + $clientSecret + '"; } }') -Encoding utf8

$files = @(Get-ChildItem "$src\src\*.cs" | ForEach-Object { $_.FullName }) + @("$out\AppInfo.g.cs", "$out\ClientInfo.g.cs") +
         @(Get-ChildItem "$src\BWUI\*.cs" | ForEach-Object { $_.FullName })

& $csc /nologo /codepage:65001 /optimize+ /target:winexe "/win32manifest:$src\BWUI\bwui.manifest" "/out:$out\TodayCalendar.exe" @refs @files
if ($LASTEXITCODE) { throw 'Build failed' }
"Built $out\TodayCalendar.exe"

if ($Snapshots) {
    $jobs = @(@('today-light', 'light'), @('today-dark', 'dark'), @('tomorrow-light', 'light', 'nextday'), @('setup-light', 'light', 'noaccount'))
    foreach ($j in $jobs) {
        $a = @('--snapshot', "$src\docs\$($j[0]).png") + $j[1..($j.Count - 1)]
        Start-Process "$out\TodayCalendar.exe" -ArgumentList $a -Wait
    }
    "Snapshots written to $src\docs"
}
if ($NoInstall) { return }

Get-Process TodayCalendar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$out\TodayCalendar.exe" $dest -Force
"Installed to $dest\TodayCalendar.exe"
