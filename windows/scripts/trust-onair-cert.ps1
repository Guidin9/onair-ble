<#
.SYNOPSIS
    Prepares a teammate's PC to run OnAirIndicator.exe: trusts the publisher certificate, clears
    the download flag, verifies the signature, and reports whether Smart App Control is in the way.

.DESCRIPTION
    Run this on the RECEIVING machine, in the folder that holds OnAirIndicator.exe and
    OnAir-Publisher.cer. No administrator rights are required -- everything happens in the
    current user's certificate stores.

    WHAT EACH STEP BUYS YOU -- read this before running:

      1. Importing the certificate  -> Windows shows the publisher's name instead of
                                       "Unknown publisher", and detects tampering.
      2. Unblock-File               -> removes the Mark-of-the-Web. THIS is the step that
                                       actually stops the "Windows protected your PC"
                                       (SmartScreen) screen from appearing. It works reliably.
      3. Smart App Control          -> NOT solved by either of the above. If SAC is enforcing on
                                       this machine, a self-signed certificate will not satisfy
                                       it. The script tells you so instead of leaving you
                                       guessing; see docs/KURULUM-WINDOWS.md.

    SECURITY -- what you are agreeing to:
      Importing a certificate into "Trusted Root Certification Authorities" means this machine
      will trust ANY software signed with the matching private key, not just this app. Only do
      this for a publisher you actually know. Windows will show a confirmation dialog; that
      dialog is the point, not an obstacle. Undo at any time with -Remove.

.PARAMETER CerPath
    The publisher certificate. Defaults to OnAir-Publisher.cer next to this script.

.PARAMETER ExePath
    The executable to unblock and verify. Defaults to OnAirIndicator.exe next to this script.

.PARAMETER Remove
    Reverses the trust: deletes the certificate from this user's Root and TrustedPublisher
    stores. Does not touch the executable.

.EXAMPLE
    .\trust-onair-cert.ps1

.EXAMPLE
    .\trust-onair-cert.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string] $CerPath = (Join-Path $PSScriptRoot 'OnAir-Publisher.cer'),
    [string] $ExePath = (Join-Path $PSScriptRoot 'OnAirIndicator.exe'),
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

# ================================================================= helpers

function Get-CertFromFile {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Certificate file not found: $Path`nIt should have been supplied together with OnAirIndicator.exe."
    }
    New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 ((Resolve-Path -LiteralPath $Path).Path)
}

function Set-CertInStore {
    <#
      Adds or removes a certificate in a CurrentUser store. The X509Store API is used rather than
      Import-Certificate / Remove-Item so that add and remove are symmetrical and so removal works
      by thumbprint regardless of how the certificate got there.
    #>
    param(
        [Parameter(Mandatory)] [string] $StoreName,
        [Parameter(Mandatory)] [System.Security.Cryptography.X509Certificates.X509Certificate2] $Cert,
        [switch] $Delete
    )

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, 'CurrentUser')
    try {
        $store.Open('ReadWrite')
        $existing = $store.Certificates | Where-Object { $_.Thumbprint -eq $Cert.Thumbprint }

        if ($Delete) {
            if ($existing) {
                $store.Remove($existing[0])
                Write-Host "  removed from CurrentUser\$StoreName" -ForegroundColor Green
            } else {
                Write-Host "  not present in CurrentUser\$StoreName - nothing to do"
            }
        } else {
            if ($existing) {
                Write-Host "  already trusted in CurrentUser\$StoreName" -ForegroundColor Green
            } else {
                # For the Root store this raises the Windows security confirmation dialog.
                $store.Add($Cert)
                Write-Host "  added to CurrentUser\$StoreName" -ForegroundColor Green
            }
        }
    } finally {
        $store.Close()
    }
}

function Get-SmartAppControlState {
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy'
    try {
        $v = (Get-ItemProperty -Path $key -ErrorAction Stop).VerifiedAndReputablePolicyState
    } catch {
        return @{ Value = $null; Text = 'not present (this Windows build has no Smart App Control)' }
    }
    switch ($v) {
        0       { @{ Value = 0; Text = 'Off' } }
        1       { @{ Value = 1; Text = 'On (enforcing)' } }
        2       { @{ Value = 2; Text = 'Evaluation' } }
        default { @{ Value = $v; Text = "unknown value '$v'" } }
    }
}

# ================================================================= remove mode

if ($Remove) {
    $cert = Get-CertFromFile -Path $CerPath
    Write-Host "Removing trust for: $($cert.Subject)"
    Write-Host "Thumbprint        : $($cert.Thumbprint)"
    Write-Host ''
    Set-CertInStore -StoreName 'Root'             -Cert $cert -Delete
    Set-CertInStore -StoreName 'TrustedPublisher' -Cert $cert -Delete
    Write-Host ''
    Write-Host 'Done. This machine no longer trusts that publisher certificate.'
    Write-Host 'The app itself is untouched; uninstall it from its own tray menu if you want it gone.'
    return
}

# ================================================================= install mode

$cert = Get-CertFromFile -Path $CerPath

Write-Host '=== On Air - preparing this PC ==='
Write-Host ''
Write-Host "Publisher  : $($cert.Subject)"
Write-Host "Valid until: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Thumbprint : $($cert.Thumbprint)"
Write-Host ''
Write-Host 'Check that thumbprint against the one the sender gave you, through a different channel'
Write-Host 'than the one the file arrived on.' -ForegroundColor Yellow
Write-Host ''

if ($cert.NotAfter -lt (Get-Date)) {
    Write-Warning 'This certificate has EXPIRED. Ask the sender for a current build.'
}

# --- 1. trust the publisher --------------------------------------------------
Write-Host '[1/4] Trusting the publisher certificate'
Write-Host '      Windows will ask you to confirm. That prompt exists because this grants trust'
Write-Host '      to anything signed with the matching key - approve it only if you know the sender.'
Set-CertInStore -StoreName 'Root'             -Cert $cert
Set-CertInStore -StoreName 'TrustedPublisher' -Cert $cert

# --- 2. clear the Mark-of-the-Web -------------------------------------------
Write-Host ''
Write-Host '[2/4] Clearing the download flag (Mark-of-the-Web)'
if (Test-Path -LiteralPath $ExePath) {
    Unblock-File -LiteralPath $ExePath
    Write-Host '  cleared - the SmartScreen "Windows protected your PC" screen will not appear' -ForegroundColor Green
} else {
    Write-Warning "  OnAirIndicator.exe not found at $ExePath - skipped. Run Unblock-File on it manually."
}

# --- 3. verify the signature -------------------------------------------------
Write-Host ''
Write-Host '[3/4] Verifying the signature'
if (Test-Path -LiteralPath $ExePath) {
    $sig = Get-AuthenticodeSignature -LiteralPath $ExePath
    Write-Host "  status: $($sig.Status)"
    switch ($sig.Status) {
        'Valid' {
            Write-Host "  signed by: $($sig.SignerCertificate.Subject)" -ForegroundColor Green
            if ($sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
                Write-Warning '  The file was signed by a DIFFERENT certificate than the one supplied. Do not run it; contact the sender.'
            }
        }
        'NotSigned' { Write-Warning '  The file is not signed at all. Ask the sender for a signed build.' }
        default     { Write-Warning "  $($sig.StatusMessage)" }
    }
} else {
    Write-Host '  skipped (executable not found)'
}

# --- 4. report Smart App Control --------------------------------------------
Write-Host ''
Write-Host '[4/4] Smart App Control'
$sac = Get-SmartAppControlState
Write-Host "  state: $($sac.Text)"

if ($sac.Value -eq 1 -or $sac.Value -eq 2) {
    Write-Host ''
    Write-Warning 'Smart App Control is active on this PC.'
    Write-Host '  A self-signed certificate does NOT satisfy it - it requires a publicly-chained'
    Write-Host '  certificate plus Microsoft reputation. If the app is blocked, you have two options:'
    Write-Host '    a) Wait. Microsoft reviews blocked files automatically; this app was cleared'
    Write-Host '       about 30 minutes after its first block on the developer machine.'
    Write-Host '    b) Turn Smart App Control off: Windows Security > App & browser control >'
    Write-Host '       Smart App Control settings > Off. Since the April 2026 update this is'
    Write-Host '       reversible - you can switch it back on whenever you like.'
    Write-Host '  See docs/KURULUM-WINDOWS.md for the full explanation.'
} else {
    Write-Host '  nothing to worry about on this machine.' -ForegroundColor Green
}

Write-Host ''
Write-Host '=== Ready. Run OnAirIndicator.exe to install. ==='
Write-Host 'To undo the trust later: .\trust-onair-cert.ps1 -Remove'
