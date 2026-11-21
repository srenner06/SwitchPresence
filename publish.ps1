#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes switch-presence as self-contained NativeAOT executables.

.DESCRIPTION
    Both executables go into ONE folder, because `switch-presence autostart on` points the
    HKCU Run key at switch-presence-bg.exe beside switch-presence.exe. Publishing them to
    separate per-project folders would break that.

    The runtime identifier defaults to this machine's architecture, so an Arm64 machine gets
    a native Arm64 build rather than an emulated x64 one.

    Needs the MSVC linker: Visual Studio with "Desktop development with C++", or the
    standalone Build Tools. NativeAOT cannot link without it.

.PARAMETER Runtime
    Overrides the detected RID, e.g. win-arm64 to cross-compile.

.PARAMETER Output
    Overrides the output folder. Defaults to .\publish\<rid>, which .gitignore covers.

.EXAMPLE
    .\publish.ps1
.EXAMPLE
    .\publish.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string]$Runtime,
    [string]$Configuration = 'Release',
    [string]$Output
)

$ErrorActionPreference = 'Stop'

if (-not $Runtime) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $Runtime = "win-$arch"
}

if (-not $Output) {
    $Output = Join-Path $PSScriptRoot "publish\$Runtime"
}

$projects = @(
    'src\SwitchPresence.Console\SwitchPresence.Console.csproj'
    'src\SwitchPresence.Background\SwitchPresence.Background.csproj'
)

Write-Host "Runtime       : $Runtime"
Write-Host "Configuration : $Configuration"
Write-Host "Output        : $Output"

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

foreach ($project in $projects) {
    $path = Join-Path $PSScriptRoot $project
    Write-Host ""
    Write-Host "Publishing $(Split-Path $project -Leaf)..."

    dotnet publish $path --configuration $Configuration --runtime $Runtime --output $Output --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project (exit $LASTEXITCODE)"
    }
}

Write-Host ""
Write-Host "Published to $Output"
Get-ChildItem $Output -Filter *.exe | ForEach-Object {
    "  {0,-24} {1,6:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
