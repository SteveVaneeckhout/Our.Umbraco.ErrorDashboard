#Requires -Version 5.1
<#
.SYNOPSIS
    Creates a self-signed TLS certificate for *.127.0.0.1.nip.io, valid for 10 years.

.DESCRIPTION
    Generates an RSA-2048 / SHA-256 self-signed certificate with a Server Authentication
    EKU and a Subject Alternative Name covering the nip.io loopback wildcard, leaves it in
    the current user's personal store, and exports it as <BaseName>.pfx (certificate plus
    private key) into the same directory as this script.

    This script does NOT need elevation. Trusting the certificate does; run
    Install-NipIoCertificate.ps1 from an elevated prompt afterwards.

.PARAMETER DnsName
    Names the certificate is valid for. Defaults to the nip.io loopback wildcard alone.

    A wildcard matches exactly one label: app.127.0.0.1.nip.io is covered,
    api.app.127.0.0.1.nip.io is not, and neither is the bare apex 127.0.0.1.nip.io.
    Pass extra names here if you need those.

.PARAMETER Years
    Validity in years from now. Defaults to 10.

.PARAMETER BaseName
    File name stem for the exported .pfx. Defaults to 'nip-io-wildcard'.

.PARAMETER OutputDirectory
    Where to write the .pfx. Defaults to the directory holding this script.

.PARAMETER PfxPassword
    Password protecting the exported .pfx. Defaults to the well-known demo password
    baked into this script so the project runs with no arguments and no prompts.

    That default is public - it is checked into the repository. It is fine for a
    throwaway localhost development certificate and nothing else. For anything real,
    pass your own:  -PfxPassword (Read-Host -AsSecureString)

.PARAMETER Force
    Overwrite existing export files instead of failing.

.EXAMPLE
    .\New-NipIoCertificate.ps1

.EXAMPLE
    .\New-NipIoCertificate.ps1 -Force -PfxPassword (Read-Host -AsSecureString)

.EXAMPLE
    .\New-NipIoCertificate.ps1 -DnsName '*.127.0.0.1.nip.io', '127.0.0.1.nip.io', 'localhost'
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string[]] $DnsName = @('*.127.0.0.1.nip.io'),

    [ValidateRange(1, 30)]
    [int] $Years = 10,

    [ValidateNotNullOrEmpty()]
    [string] $BaseName = 'nip-io-wildcard',

    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = $PSScriptRoot,

    # Demo default: public on purpose, so a fresh clone runs with no arguments.
    # Keep in step with the same default in Install-NipIoCertificate.ps1.
    [securestring] $PfxPassword = (ConvertTo-SecureString 'RVnVCUizRilozcSXuO8cMfG5SAZ4tCj4' -AsPlainText -Force),

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PfxPassword.Length -eq 0) {
    throw 'The .pfx password cannot be empty.'
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
}
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath

$pfxPath = Join-Path $OutputDirectory ($BaseName + '.pfx')

if ((Test-Path -LiteralPath $pfxPath) -and -not $Force) {
    throw "'$pfxPath' already exists. Re-run with -Force to overwrite it."
}

# Clock skew between this machine and anything validating the cert can make a
# just-issued certificate look not-yet-valid, so back-date slightly.
$notBefore = (Get-Date).AddMinutes(-10)
$notAfter  = $notBefore.AddYears($Years)

Write-Verbose "Creating certificate for: $($DnsName -join ', ')"

$cert = New-SelfSignedCertificate `
    -Subject ('CN=' + $DnsName[0]) `
    -DnsName $DnsName `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -FriendlyName "nip.io local development ($($DnsName[0]))" `
    -NotBefore $notBefore `
    -NotAfter $notAfter `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -TextExtension @(
        '2.5.29.37={text}1.3.6.1.5.5.7.3.1'   # EKU: Server Authentication
        '2.5.29.19={text}'                     # Basic Constraints: end entity, not a CA
    )

try {
    $null = Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $PfxPassword -Force
}
catch {
    # Do not leave a half-exported certificate sitting in the personal store.
    Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $cert.Thumbprint) -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host ''
Write-Host 'Certificate created.' -ForegroundColor Green
Write-Host ("  Subject     : {0}" -f $cert.Subject)
Write-Host ("  SANs        : {0}" -f ($DnsName -join ', '))
Write-Host ("  Thumbprint  : {0}" -f $cert.Thumbprint)
Write-Host ("  Valid until : {0:yyyy-MM-dd}" -f $cert.NotAfter)
Write-Host ("  Store       : Cert:\CurrentUser\My\{0}" -f $cert.Thumbprint)
Write-Host ("  PFX         : {0}" -f $pfxPath)
Write-Host ''
Write-Host 'Next: trust it (elevated PowerShell):' -ForegroundColor Cyan
Write-Host '  .\Install-NipIoCertificate.ps1'
Write-Host ''

[pscustomobject]@{
    Thumbprint = $cert.Thumbprint
    Subject    = $cert.Subject
    DnsName    = $DnsName
    NotAfter   = $cert.NotAfter
    PfxPath    = $pfxPath
}
