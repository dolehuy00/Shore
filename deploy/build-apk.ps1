# Builds artifacts\apk\Shorekeeper-<version>.apk signed with the release keystore (docs/14-android.md A6).
# The keystore and its password never live in the repo:
#   $env:SHOREKEEPER_KEYSTORE           path to the keystore (default: ~\.shorekeeper-signing\shorekeeper-release.keystore)
#   $env:SHOREKEEPER_KEYSTORE_PASSWORD  its password (store and key share it)
#   powershell -ExecutionPolicy Bypass -File deploy\build-apk.ps1
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Shorekeeper.Android\Shorekeeper.Android.csproj'
$publish = Join-Path $root 'artifacts\publish\android'
$output = Join-Path $root 'artifacts\apk'

$keyStore = $env:SHOREKEEPER_KEYSTORE
if (-not $keyStore) {
    $keyStore = Join-Path $HOME '.shorekeeper-signing\shorekeeper-release.keystore'
}
if (-not (Test-Path $keyStore)) { throw "Keystore not found: $keyStore" }
if (-not $env:SHOREKEEPER_KEYSTORE_PASSWORD) { throw 'Set SHOREKEEPER_KEYSTORE_PASSWORD.' }

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

# env: makes the Android tooling read the password from the environment, so it never appears on a command line.
dotnet publish $project -c $Configuration -o $publish `
    -p:AndroidPackageFormat=apk `
    -p:AndroidKeyStore=true `
    "-p:AndroidSigningKeyStore=$keyStore" `
    -p:AndroidSigningKeyAlias=shorekeeper `
    -p:AndroidSigningStorePass=env:SHOREKEEPER_KEYSTORE_PASSWORD `
    -p:AndroidSigningKeyPass=env:SHOREKEEPER_KEYSTORE_PASSWORD
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$version = (dotnet msbuild $project -getProperty:ApplicationDisplayVersion).Trim()
New-Item -ItemType Directory -Force $output | Out-Null
$apk = Join-Path $output "Shorekeeper-$version.apk"
Copy-Item (Get-ChildItem $publish -Filter '*-Signed.apk' | Select-Object -First 1).FullName $apk -Force

"{0}  {1:N1} MB" -f $apk, ((Get-Item $apk).Length / 1MB)
