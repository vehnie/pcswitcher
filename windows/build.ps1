# Builds the Windows agent with the C# compiler that ships with Windows (.NET Framework 4.8, C# 5).
# No SDK needed. Output in .\bin:
#   mas.exe                console build: CLI / diagnostics
#   MonitorAutoSwitch.exe  windowless build: tray agent (what start-at-login runs)
#
#   .\build.ps1              build
#   .\build.ps1 -Install     build, copy to %LOCALAPPDATA%\Programs\MonitorAutoSwitch, enable start at login, start agent
param([switch]$Install)
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$common = @('/nologo', '/optimize+', '/platform:anycpu', '/nowarn:1702',
            '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

& $csc @common /target:exe "/out:$bin\mas.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'build of mas.exe failed' }
& $csc @common /target:winexe /define:AGENT_EXE "/out:$bin\MonitorAutoSwitch.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'build of MonitorAutoSwitch.exe failed' }
Write-Host "Built $bin\mas.exe and $bin\MonitorAutoSwitch.exe"

if ($Install) {
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\MonitorAutoSwitch'
    New-Item -ItemType Directory -Force $dest | Out-Null
    # Stop a running agent so its exe can be replaced.
    Get-Process MonitorAutoSwitch -ErrorAction SilentlyContinue | Stop-Process -Force -PassThru | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    # Windows can keep the exe locked briefly after the process exits; retry for a few seconds.
    for ($i = 1; ; $i++) {
        try { Copy-Item "$bin\mas.exe", "$bin\MonitorAutoSwitch.exe" $dest -Force -ErrorAction Stop; break }
        catch { if ($i -ge 20) { throw }; Start-Sleep -Milliseconds 500 }
    }
    Write-Host "Installed to $dest (add it to PATH to use 'mas' anywhere)"
    & "$dest\mas.exe" install-agent
}
