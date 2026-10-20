<#
.SYNOPSIS
    Signs the published OnAirIndicator.exe with a self-signed code-signing certificate and
    exports the PUBLIC half of that certificate for distribution.

.DESCRIPTION
    Run this on the DEVELOPER machine after `dotnet publish`. It produces two files to hand out:

        OnAirIndicator.exe    the signed executable
        OnAir-Publisher.cer   the public certificate teammates import (see trust-onair-cert.ps1)

    The certificate is created once and reused on every later run, keyed by its subject. That is
    deliberate: teammates import the .cer a single time and every future release validates against
    it. Generating a fresh certificate per build would force everyone to re-import each time.

    WHAT THIS DOES AND DOES NOT ACHIEVE
      * DOES  : replaces "Unknown publisher" with your name, and makes tampering detectable --
                any modification after signing invalidates the signature.
      * DOES  : nothing at all unless the recipient imports the .cer (see trust-onair-cert.ps1).
      * NOT   : it does NOT satisfy Smart App Control, and it does NOT earn SmartScreen
                reputation. Both require a publicly-chained certificate plus Microsoft's
                reputation service. This was measured on 2026-08-05, not assumed --
                see docs/KURULUM-WINDOWS.md.

    THE PRIVATE KEY NEVER LEAVES THIS MACHINE. This script only ever calls Export-Certificate
    (public key). It never calls Export-PfxCertificate. Do not add that.

.PARAMETER ExePath
    The executable to sign. Defaults to the single-file publish output.

.PARAMETER CertSubject
    Subject of the code-signing certificate. Change it once, before the first release; changing it
    later invalidates every .cer teammates have already imported.

.PARAMETER OutCer
    Where to write the public certificate. Defaults to alongside the signed EXE.

.PARAMETER TimestampServer
    RFC 3161 timestamp authority. Timestamping keeps the signature valid after the certificate
    expires. Pass an empty string to skip it (e.g. when offline).

.EXAMPLE
    .\scripts\sign-selfsigned.ps1

.EXAMPLE
    .\scripts\sign-selfsigned.ps1 -ExePath 'C:\dist\OnAirIndicator.exe' -OutCer 'C:\dist\OnAir-Publisher.cer'
#>
[CmdletBinding()]
param(
    [string] $ExePath = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\OnAirIndicator.exe'),
    [string] $CertSubject = 'CN=On Air Indicator',
    [string] $OutCer = '',
    [string] $TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- resolve inputs

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath`nRun 'dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true' first."
}
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path

if ([string]::IsNullOrWhiteSpace($OutCer)) {
    $OutCer = Join-Path (Split-Path -Parent $ExePath) 'OnAir-Publisher.cer'
}

Write-Host "Executable : $ExePath"
Write-Host "Subject    : $CertSubject"
Write-Host ''

# ---------------------------------------------------------------- find or create the certificate

# Reuse an existing, still-valid certificate so the .cer teammates hold keeps working.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($cert) {
    Write-Host "Reusing certificate $($cert.Thumbprint) (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))." -ForegroundColor Green
} else {
    Write-Host 'No usable certificate found - creating one (valid 3 years).' -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertSubject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(3)
    Write-Host "Created certificate $($cert.Thumbprint)." -ForegroundColor Green
    Write-Warning 'This is a NEW certificate. Everyone must re-import the .cer produced below.'
}

# ---------------------------------------------------------------- sign

Write-Host ''
Write-Host 'Signing...'

$signArgs = @{
    FilePath      = $ExePath
    Certificate   = $cert
    HashAlgorithm = 'SHA256'
}
if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
    $signArgs['TimestampServer'] = $TimestampServer
}

try {
    $result = Set-AuthenticodeSignature @signArgs
} catch {
    # An unreachable timestamp authority must not cost you the signature.
    if ($signArgs.ContainsKey('TimestampServer')) {
        Write-Warning "Timestamping failed ($($_.Exception.Message)). Signing without a timestamp."
        $signArgs.Remove('TimestampServer')
        $result = Set-AuthenticodeSignature @signArgs
    } else {
        throw
    }
}

# 'UnknownError' here is EXPECTED and not a failure: the signature was written, but this machine
# does not trust the self-signed root, so verification cannot complete. trust-onair-cert.ps1 is
# what makes it report 'Valid'. Anything else is a real problem.
if ($result.Status -notin @('Valid', 'UnknownError')) {
    throw "Signing failed: $($result.Status) - $($result.StatusMessage)"
}
Write-Host "Signature written (verification status here: $($result.Status))." -ForegroundColor Green

# ---------------------------------------------------------------- export the public certificate

# Export-Certificate writes the public key only. Never swap this for Export-PfxCertificate.
Export-Certificate -Cert $cert -FilePath $OutCer -Type CERT | Out-Null
Write-Host "Public certificate exported: $OutCer" -ForegroundColor Green

# ---------------------------------------------------------------- summary

$hash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash

Write-Host ''
Write-Host '--- Ship these three files together ---'
Write-Host "  1. $ExePath"
Write-Host "  2. $OutCer"
Write-Host "  3. docs\KURULUM-WINDOWS.md  (and scripts\trust-onair-cert.ps1)"
Write-Host ''
Write-Host "SHA-256: $hash"
Write-Host 'Publish this hash alongside the download so recipients can verify what they got.'
Write-Host ''
Write-Host 'Reminder: the hash changed, so any Smart App Control / SmartScreen reputation the'
Write-Host 'previous build had earned does not carry over to this one.' -ForegroundColor Yellow
