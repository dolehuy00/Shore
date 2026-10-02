# Builds artifacts\msi\Shorekeeper-<version>-x64.msi: publishes the app self-contained for win-x64,
# then packs that folder with WiX v5 (docs/11-windows-deployment.md §2).
#   powershell -ExecutionPolicy Bypass -File deploy\build-msi.ps1
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish\win-x64'
$output = Join-Path $root 'artifacts\msi'

# Start clean: every file in the folder ends up in the MSI.
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

dotnet publish (Join-Path $root 'src\Shorekeeper.Desktop\Shorekeeper.Desktop.csproj') `
    -c $Configuration -r win-x64 --self-contained -o $publish -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

dotnet build (Join-Path $PSScriptRoot 'msi\Shorekeeper.Installer.wixproj') `
    -c $Configuration -o $output "-p:AppPublishDir=$publish\"
if ($LASTEXITCODE -ne 0) { throw "WiX build failed ($LASTEXITCODE)" }

Get-ChildItem $output -Filter *.msi | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
