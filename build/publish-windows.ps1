# Publishes the Windows desktop app. Needs Windows and the maui-windows workload.
# Always produces a self-contained folder in artifacts/packages/windows-<runtime>.
# -Pack turns it into an installer and update packages in artifacts/releases/windows-<runtime>
# (Velopack; the vpk tool comes from dotnet-tools.json). -PreviousReleasesFrom fetches the latest
# published release first, so the packages include a delta from it: from a GitHub repository URL,
# or a folder an earlier -Pack produced (to try deltas locally).
# -NoPublish packs a folder published earlier (a release signs and packs the folder CI built).
# -AzureTrustedSignFile signs the app and installer with Azure Artifact Signing: the path of its
# metadata.json, with an Azure sign-in already in place (az login, or azure/login in a workflow).
param(
    [string]$Version = $(if ($env:OFFLOADR_RUNNER_VERSION) { $env:OFFLOADR_RUNNER_VERSION } else { '0.1.0' }),
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Pack,
    [switch]$NoPublish,
    [string]$PreviousReleasesFrom,
    [string]$AzureTrustedSignFile
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows publishing requires Windows and the maui-windows workload.' }

# Paths the caller gave are relative to their PowerShell location, which .NET does not know about.
if ($AzureTrustedSignFile) {
    if (-not (Test-Path -LiteralPath $AzureTrustedSignFile -PathType Leaf)) { throw "There is no signing metadata file at '$AzureTrustedSignFile'." }
    $AzureTrustedSignFile = (Resolve-Path -LiteralPath $AzureTrustedSignFile).ProviderPath
}
$previousFolder = $PreviousReleasesFrom -and (Test-Path -LiteralPath $PreviousReleasesFrom -PathType Container)
if ($previousFolder) { $PreviousReleasesFrom = (Resolve-Path -LiteralPath $PreviousReleasesFrom).ProviderPath }

$repository = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $repository "artifacts/packages/windows-$Runtime"
$releasesDir = Join-Path $repository "artifacts/releases/windows-$Runtime"
$packId = 'app.offloadr.runner'

if ($NoPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'OffloadrRunner.exe'))) { throw "There is no published app in $publishDir to pack." }
}
else {
    # Start from an empty folder: anything left from an earlier run (such as a browser profile from
    # starting the published app) would otherwise ship in the installer.
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish (Join-Path $repository 'src/Offloadr.Runner.Desktop/Offloadr.Runner.Desktop.csproj') `
        -c Release `
        -f net10.0-windows10.0.19041.0 `
        "-p:RuntimeIdentifierOverride=$Runtime" `
        "-p:VersionPrefix=$Version" `
        -p:SelfContained=true `
        -p:WindowsAppSDKSelfContained=true `
        -p:WindowsPackageType=None `
        -o $publishDir
    if ($LASTEXITCODE) { throw 'Windows publish failed.' }

    # The app's licence and the notices of what it bundles travel with it.
    Copy-Item (Join-Path $repository 'LICENSE') (Join-Path $publishDir 'LICENSE.txt')
    Copy-Item (Join-Path $repository 'THIRD-PARTY-NOTICES.md') (Join-Path $publishDir 'THIRD-PARTY-NOTICES.md')
}

if (-not $Pack) { return }

Push-Location $repository
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Could not restore the vpk tool.' }

    # The earlier release is copied aside first: it may be this script's own output folder, which
    # is emptied next (pack one version, then the next from the same folder).
    $staged = $null
    if ($previousFolder) {
        $staged = Join-Path ([IO.Path]::GetTempPath()) ("offloadr-previous-release-" + [Guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory $staged | Out-Null
        Copy-Item (Join-Path $PreviousReleasesFrom '*') $staged
    }

    if (Test-Path $releasesDir) { Remove-Item $releasesDir -Recurse -Force }
    New-Item -ItemType Directory $releasesDir | Out-Null
    # One update channel per architecture: an installed app only ever updates from its own.
    $channel = $Runtime
    if ($staged) {
        Copy-Item (Join-Path $staged '*') $releasesDir
        Remove-Item $staged -Recurse -Force
    }
    elseif ($PreviousReleasesFrom) {
        # There is nothing to download before the first release; packing then makes no delta.
        dotnet vpk download github --repoUrl $PreviousReleasesFrom --channel $channel -o $releasesDir
        if ($LASTEXITCODE) { Write-Warning 'No earlier release was downloaded, so no delta package is made.' }
    }

    $signing = @()
    if ($AzureTrustedSignFile) { $signing = @('--azureTrustedSignFile', $AzureTrustedSignFile) }

    dotnet vpk pack @signing `
        --packId $packId `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe OffloadrRunner.exe `
        --packTitle 'Offloadr Runner' `
        --packAuthors 'Offloadr' `
        --icon (Join-Path $publishDir 'appicon.ico') `
        --channel $channel `
        --runtime $Runtime `
        --shortcuts StartMenuRoot `
        --noPortable `
        --exclude '.*\.pdb|.*\.WebView2([\\/].*)?' `
        -o $releasesDir
    if ($LASTEXITCODE) { throw 'Packing the installer failed.' }
}
finally {
    Pop-Location
}

# The earlier release only served to make the delta; it is already published. Its installer was
# replaced by this one, which has the same name.
Get-ChildItem $releasesDir -Filter '*.nupkg' |
    Where-Object { $_.Name -notlike "$packId-$Version-*" } |
    Remove-Item
Get-ChildItem $releasesDir | ForEach-Object { '{0,12}  {1}' -f $_.Length, $_.Name }
