# Checks the Authenticode signatures of a packed desktop release: the installer, and the app's own
# executables and assemblies inside its full update package (the app, the updater and the launcher
# stub). -Expect Signed requires a valid signature on each, from
# -Subject when given; -Expect Unsigned requires none, so an unsigned release is never mistaken
# for a signed one. Third-party files keep their own publishers' signatures and are not checked.
param(
    [Parameter(Mandatory)][string]$ReleasesDir,
    [Parameter(Mandatory)][ValidateSet('Signed', 'Unsigned')][string]$Expect,
    [string]$Subject
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$setup = @(Get-ChildItem $ReleasesDir -Filter '*-Setup.exe')
$package = @(Get-ChildItem $ReleasesDir -Filter '*-full.nupkg')
if ($setup.Count -ne 1 -or $package.Count -ne 1) { throw "Expected one installer and one full package in $ReleasesDir." }

$extracted = Join-Path ([IO.Path]::GetTempPath()) ("offloadr-signatures-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory $extracted | Out-Null
try {
    # Squirrel.exe becomes the installed Update.exe. RestartAgent.exe is the Windows App SDK's own.
    $own = '^lib/app/(OffloadrRunner\.(exe|dll)|Offloadr\.Runner\..+\.dll|Squirrel\.exe|.+_ExecutionStub\.exe)$'
    $zip = [IO.Compression.ZipFile]::OpenRead($package[0].FullName)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -match $own })
        foreach ($entry in $entries) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $extracted $entry.Name))
        }
    }
    finally {
        $zip.Dispose()
    }

    # Each of these must be in the package, or the check would pass on a package that lacks them.
    foreach ($required in 'OffloadrRunner.exe', 'Squirrel.exe') {
        if (-not (Test-Path (Join-Path $extracted $required))) { throw "The full package has no $required." }
    }
    if (-not (Get-ChildItem $extracted -Filter '*_ExecutionStub.exe')) { throw 'The full package has no launcher stub.' }

    $files = @($setup[0]) + @(Get-ChildItem $extracted -File)
    $failures = @()
    foreach ($file in $files) {
        $signature = Get-AuthenticodeSignature $file.FullName
        $signer = $signature.SignerCertificate.Subject
        "{0,-10} {1}{2}" -f $signature.Status, $file.Name, $(if ($signer) { "  ($signer)" } else { '' })
        if ($Expect -eq 'Signed') {
            if ($signature.Status -ne 'Valid') { $failures += "$($file.Name): $($signature.Status)" }
            elseif ($Subject -and $signer -notlike "*$Subject*") { $failures += "$($file.Name): signed by $signer" }
        }
        elseif ($signature.Status -ne 'NotSigned') {
            $failures += "$($file.Name): $($signature.Status), expected no signature"
        }
    }

    if ($failures) { throw "Signature check failed:`n$($failures -join "`n")" }
    "All $($files.Count) files are $($Expect.ToLowerInvariant())."
}
finally {
    Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue
}
