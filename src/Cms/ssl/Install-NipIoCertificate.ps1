#Requires -Version 5.1
<#
.SYNOPSIS
    Trusts a certificate machine-wide by adding it to Trusted Root Certification Authorities.

.DESCRIPTION
    Adds the public part of a certificate to Cert:\LocalMachine\Root, which is the store
    Windows, Chrome and Edge consult, so they stop warning about it. Optionally also imports
    the private key into Cert:\LocalMachine\My so a service (IIS, Kestrel, a reverse proxy)
    running as a machine account can present the certificate.

    Only the public certificate ever goes into the Root store - the private key is never
    written there, whichever input you give it.

    MUST be run elevated. The script refuses to continue otherwise.

.PARAMETER CertificatePath
    Path to a .pfx / .p12 (with private key) or .cer (public only) file. Defaults to the
    .pfx that New-NipIoCertificate.ps1 writes next to this script.

.PARAMETER PfxPassword
    Password for the .pfx. Defaults to the same well-known demo password that
    New-NipIoCertificate.ps1 uses, so the pair works with no arguments and no prompts.
    Pass your own if you generated the .pfx with a different one.

.PARAMETER Thumbprint
    Alternative to -CertificatePath: trust a certificate that is already in
    Cert:\CurrentUser\My or Cert:\LocalMachine\My, by thumbprint.

.PARAMETER InstallPrivateKey
    Also import the certificate and its private key into Cert:\LocalMachine\My.
    Requires a .pfx input. Use this if a Windows service needs to serve the certificate.

.PARAMETER Uninstall
    Remove the certificate from Cert:\LocalMachine\Root and Cert:\LocalMachine\My instead
    of adding it. The thumbprint to remove is read out of the .pfx beside this script
    (or whatever -CertificatePath / -Thumbprint point at), so no argument is needed.

.EXAMPLE
    # From an elevated PowerShell prompt
    .\Install-NipIoCertificate.ps1

.EXAMPLE
    .\Install-NipIoCertificate.ps1 -InstallPrivateKey

.EXAMPLE
    # Undo it; the thumbprint comes from the .pfx in this directory
    .\Install-NipIoCertificate.ps1 -Uninstall
#>
[CmdletBinding(DefaultParameterSetName = 'ByPath', SupportsShouldProcess = $true)]
param(
    [Parameter(ParameterSetName = 'ByPath', Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string] $CertificatePath = (Join-Path $PSScriptRoot 'nip-io-wildcard.pfx'),

    # Demo default: public on purpose, so a fresh clone runs with no arguments.
    # Keep in step with the same default in New-NipIoCertificate.ps1.
    [Parameter(ParameterSetName = 'ByPath')]
    [securestring] $PfxPassword = (ConvertTo-SecureString 'RVnVCUizRilozcSXuO8cMfG5SAZ4tCj4' -AsPlainText -Force),

    [Parameter(ParameterSetName = 'ByThumbprint', Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $Thumbprint,

    [Parameter(ParameterSetName = 'ByPath')]
    [switch] $InstallPrivateKey,

    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- elevation -------------------------------------------------------------

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host ''
    Write-Host 'This script must run elevated - writing to the machine Root store needs admin rights.' -ForegroundColor Red
    Write-Host 'Start an elevated PowerShell and re-run, or:' -ForegroundColor Yellow
    Write-Host ("  Start-Process powershell -Verb RunAs -ArgumentList '-NoExit','-File','{0}'" -f $PSCommandPath)
    Write-Host ''
    exit 1
}

# --- load the certificate --------------------------------------------------

$isPfx = $false

if ($PSCmdlet.ParameterSetName -eq 'ByThumbprint') {
    $cert = Get-ChildItem -Path 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Thumbprint } |
        Select-Object -First 1

    if (-not $cert) {
        throw "No certificate with thumbprint '$Thumbprint' found in Cert:\CurrentUser\My or Cert:\LocalMachine\My."
    }
}
else {
    if (-not (Test-Path -LiteralPath $CertificatePath)) {
        throw "Certificate file not found: '$CertificatePath'. Run New-NipIoCertificate.ps1 first, or pass -CertificatePath."
    }
    $CertificatePath = (Resolve-Path -LiteralPath $CertificatePath).ProviderPath
    $isPfx = [IO.Path]::GetExtension($CertificatePath) -in @('.pfx', '.p12')

    if ($isPfx) {
        try {
            $cert = (Get-PfxData -FilePath $CertificatePath -Password $PfxPassword).EndEntityCertificates[0]
        }
        catch {
            throw ("Could not open '{0}'. If it was created with a password other than the demo default, pass it with -PfxPassword. ({1})" -f $CertificatePath, $_.Exception.Message)
        }
    }
    else {
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath)
    }
}

if ($InstallPrivateKey -and -not $isPfx) {
    throw '-InstallPrivateKey needs a .pfx input; a .cer carries no private key.'
}

$subject = $cert.Subject
$thumb   = $cert.Thumbprint

Write-Host ''
Write-Host ("Certificate : {0}" -f $subject)
Write-Host ("Thumbprint  : {0}" -f $thumb)
Write-Host ("Valid until : {0:yyyy-MM-dd}" -f $cert.NotAfter)
Write-Host ''

# --- Trusted Root Certification Authorities (machine) ----------------------

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
try {
    $store.Open('ReadWrite')
    $existing = $store.Certificates.Find('FindByThumbprint', $thumb, $false)

    if ($Uninstall) {
        if ($existing.Count -eq 0) {
            Write-Host 'Not present in Cert:\LocalMachine\Root - nothing to remove.' -ForegroundColor Yellow
        }
        elseif ($PSCmdlet.ShouldProcess('Cert:\LocalMachine\Root', "Remove $thumb")) {
            $store.RemoveRange($existing)
            Write-Host 'Removed from Cert:\LocalMachine\Root.' -ForegroundColor Green
        }
    }
    elseif ($existing.Count -gt 0) {
        Write-Host 'Already trusted in Cert:\LocalMachine\Root - nothing to do.' -ForegroundColor Yellow
    }
    elseif ($PSCmdlet.ShouldProcess('Cert:\LocalMachine\Root', "Add $thumb")) {
        # Import the raw public certificate only: never put a private key in the Root store.
        $publicOnly = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, $cert.RawData)
        $store.Add($publicOnly)
        Write-Host 'Added to Cert:\LocalMachine\Root (Trusted Root Certification Authorities).' -ForegroundColor Green
    }
}
finally {
    $store.Close()
    if ($store -is [IDisposable]) { $store.Dispose() }
}

# --- the machine personal store --------------------------------------------
# On uninstall this runs unconditionally, so one -Uninstall undoes an install that
# used -InstallPrivateKey without having to remember that it did.

if ($InstallPrivateKey -or $Uninstall) {
    $inMy = Get-ChildItem -Path 'Cert:\LocalMachine\My' | Where-Object { $_.Thumbprint -eq $thumb }

    if ($Uninstall) {
        if ($inMy -and $PSCmdlet.ShouldProcess('Cert:\LocalMachine\My', "Remove $thumb")) {
            Remove-Item -LiteralPath ('Cert:\LocalMachine\My\' + $thumb) -DeleteKey -Force
            Write-Host 'Removed from Cert:\LocalMachine\My.' -ForegroundColor Green
        }
    }
    elseif ($inMy) {
        Write-Host 'Already present in Cert:\LocalMachine\My - nothing to do.' -ForegroundColor Yellow
    }
    elseif ($PSCmdlet.ShouldProcess('Cert:\LocalMachine\My', "Import $thumb with private key")) {
        $null = Import-PfxCertificate -FilePath $CertificatePath `
                                      -CertStoreLocation 'Cert:\LocalMachine\My' `
                                      -Password $PfxPassword `
                                      -Exportable
        Write-Host 'Imported into Cert:\LocalMachine\My with its private key.' -ForegroundColor Green
    }
}

if (-not $Uninstall) {
    Write-Host ''
    Write-Host 'Restart the browser completely (all windows) before testing.' -ForegroundColor Cyan
    Write-Host 'Firefox keeps its own trust store; it reads the Windows one only while'
    Write-Host 'security.enterprise_roots.enabled is true in about:config (the default).'
    Write-Host ''
    Write-Host 'To undo (reads the thumbprint back out of the .pfx beside the script):' -ForegroundColor Cyan
    Write-Host '  .\Install-NipIoCertificate.ps1 -Uninstall'
    Write-Host ''
}
