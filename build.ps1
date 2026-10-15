# Builds Prayer Times, installs it to %LOCALAPPDATA%\Prayer Times and launches it.
# Uses the C# compiler that ships with Windows, so nothing extra needs to be installed.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'Could not find the .NET Framework C# compiler (csc.exe).' }

if (Test-Path build) { Remove-Item build -Recurse -Force }
New-Item -ItemType Directory build | Out-Null

& $csc /nologo /target:winexe /optimize+ /out:build\PrayerTimes.exe /win32icon:Resources\icon.ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll src\PrayerTimes.cs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Copy-Item Resources\timetable.json build\
# Sounds are optional: without them the app uses built-in Windows sounds.
foreach ($sound in 'reminder', 'call_to_prayer') {
    if (Test-Path "Resources\$sound.mp3") { Copy-Item "Resources\$sound.mp3" build\ }
}

Get-Process PrayerTimes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$dest = Join-Path $env:LOCALAPPDATA 'Prayer Times'
New-Item -ItemType Directory $dest -Force | Out-Null
Copy-Item build\* $dest -Force
Start-Process (Join-Path $dest 'PrayerTimes.exe')
Write-Host "Installed to $dest"
