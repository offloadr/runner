# Publishes the Windows desktop app. Needs Windows and the maui-windows workload.
# -Unpackaged produces a self-contained folder in artifacts/packages/windows-<runtime>;
# otherwise an unsigned MSIX is produced, which must be signed before it can be installed.
param(
    [string]$Version = $(if ($env:OFFLOADR_RUNNER_VERSION) { $env:OFFLOADR_RUNNER_VERSION } else { '0.1.0' }),
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$SigningProperties,
    [switch]$Unpackaged
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows publishing requires Windows and the maui-windows workload.' }

$repository = Split-Path $PSScriptRoot -Parent
$arguments = @(
    'publish', (Join-Path $repository 'src/Offloadr.Runner.Desktop/Offloadr.Runner.Desktop.csproj'),
    '-c', 'Release',
    '-f', 'net10.0-windows10.0.19041.0',
    "-p:RuntimeIdentifierOverride=$Runtime",
    "-p:VersionPrefix=$Version",
    '-p:SelfContained=true',
    '-p:WindowsAppSDKSelfContained=true'
)
if ($Unpackaged) {
    $arguments += @('-p:WindowsPackageType=None', '-o', (Join-Path $repository "artifacts/packages/windows-$Runtime"))
}
else {
    $arguments += @('-p:WindowsPackageType=MSIX', '-p:GenerateAppxPackageOnBuild=true', '-p:AppxBundle=Never')
    if (-not $SigningProperties) { $arguments += '-p:AppxPackageSigningEnabled=false' }
}
if ($SigningProperties) { $arguments += "-p:CustomAfterMicrosoftCommonTargets=$([IO.Path]::GetFullPath($SigningProperties))" }

dotnet @arguments
if ($LASTEXITCODE) { throw 'Windows publish failed.' }
if (-not $Unpackaged -and -not $SigningProperties) {
    Write-Output 'The MSIX package is unsigned; sign it before installing.'
}
