[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Sign,

    [ValidateSet("win-x64", "win-arm64")]
    [string[]]$RuntimeIdentifier = @("win-x64", "win-arm64"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDirectory,

    [string]$CertificateThumbprint,

    [uri]$TimestampUrl,

    [string]$SignToolPath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-SignToolPath {
    param(
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "SignTool was not found at '$RequestedPath'."
        }

        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $windowsKitsRoot -PathType Container)) {
        throw "x86 SignTool was not found. Install the Windows SDK or pass -SignToolPath."
    }

    $candidate = Get-ChildItem -LiteralPath $windowsKitsRoot -Filter "signtool.exe" -File -Recurse |
        Where-Object { $_.Directory.Name -eq "x86" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -ne $candidate) {
        return $candidate.FullName
    }

    throw "x86 SignTool was not found. Install the Windows SDK or pass -SignToolPath."
}

$signRequested = $Sign.IsPresent
foreach ($argument in $RemainingArguments) {
    if ($argument -eq "--sign") {
        $signRequested = $true
        continue
    }

    throw "Unknown argument '$argument'."
}

$normalizedThumbprint = $null
$certificate = $null
$resolvedSignToolPath = $null
$logDirectory = $null

if ($signRequested) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $CertificateThumbprint = $env:CODE_SIGNING_CERTIFICATE_THUMBPRINT
        if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
            $CertificateThumbprint = [Environment]::GetEnvironmentVariable(
                "CODE_SIGNING_CERTIFICATE_THUMBPRINT",
                "User"
            )
        }
    }

    if ($null -eq $TimestampUrl) {
        $timestampUrlValue = $env:CODE_SIGNING_TIMESTAMP_URL
        if ([string]::IsNullOrWhiteSpace($timestampUrlValue)) {
            $timestampUrlValue = [Environment]::GetEnvironmentVariable(
                "CODE_SIGNING_TIMESTAMP_URL",
                "User"
            )
        }

        if (-not [string]::IsNullOrWhiteSpace($timestampUrlValue)) {
            $TimestampUrl = [uri]$timestampUrlValue
        }
    }

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw (
            "--sign requires -CertificateThumbprint or " +
            "CODE_SIGNING_CERTIFICATE_THUMBPRINT."
        )
    }

    if ($null -eq $TimestampUrl) {
        throw "--sign requires -TimestampUrl or CODE_SIGNING_TIMESTAMP_URL."
    }

    if (
        -not $TimestampUrl.IsAbsoluteUri -or
        $TimestampUrl.Scheme -notin @([uri]::UriSchemeHttp, [uri]::UriSchemeHttps)
    ) {
        throw "TimestampUrl must be an absolute HTTP or HTTPS URL."
    }

    $normalizedThumbprint = ($CertificateThumbprint -replace "\s", "").ToUpperInvariant()
    if ($normalizedThumbprint -notmatch "^[0-9A-F]{40}$") {
        throw "CertificateThumbprint must be a 40-character SHA-1 certificate thumbprint."
    }

    $certificatePath = "Cert:\CurrentUser\My\$normalizedThumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath)) {
        throw "Code-signing certificate '$normalizedThumbprint' was not found in Cert:\CurrentUser\My."
    }

    $certificate = Get-Item -LiteralPath $certificatePath
    if (-not $certificate.HasPrivateKey) {
        throw "Certificate '$normalizedThumbprint' has no accessible private key."
    }

    if ($certificate.NotAfter -le (Get-Date)) {
        throw "Certificate '$normalizedThumbprint' expired on $($certificate.NotAfter.ToString('u'))."
    }

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    if ($certificate.EnhancedKeyUsageList.ObjectId -notcontains $codeSigningOid) {
        throw "Certificate '$normalizedThumbprint' is not valid for code signing."
    }

    $resolvedSignToolPath = Resolve-SignToolPath -RequestedPath $SignToolPath
    $logDirectory = Join-Path $PSScriptRoot "artifacts\logs"
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}

$projectPath = Join-Path $PSScriptRoot "src\ServiceDeskCallSimulator\ServiceDeskCallSimulator.csproj"
$executableName = "ServiceDeskCallSimulator.exe"
$runtimeIdentifiers = @($RuntimeIdentifier | Select-Object -Unique)
$resolvedCustomOutputDirectory = $null

if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $resolvedCustomOutputDirectory =
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
}

foreach ($runtime in $runtimeIdentifiers) {
    $stagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
        "ServiceDeskCallSimulator-publish-{0}" -f [guid]::NewGuid()
    )

    if ($null -eq $resolvedCustomOutputDirectory) {
        $resolvedOutputDirectory = Join-Path $PSScriptRoot "artifacts\publish\$runtime"
    }
    elseif ($runtimeIdentifiers.Count -eq 1) {
        $resolvedOutputDirectory = $resolvedCustomOutputDirectory
    }
    else {
        $resolvedOutputDirectory = Join-Path $resolvedCustomOutputDirectory $runtime
    }

    try {
        New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

        Write-Host "Publishing $runtime single-file, self-contained, untrimmed executable..."
        & dotnet publish $projectPath `
            --configuration $Configuration `
            --runtime $runtime `
            --self-contained true `
            --output $stagingDirectory `
            --nologo `
            -p:PublishSingleFile=true `
            -p:PublishTrimmed=false `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:IncludeAllContentForSelfExtract=true `
            -p:DebugType=None `
            -p:DebugSymbols=false

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish for $runtime failed with exit code $LASTEXITCODE."
        }

        $publishedFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse)
        $publishedExecutable = Join-Path $stagingDirectory $executableName
        if ($publishedFiles.Count -ne 1 -or -not (Test-Path -LiteralPath $publishedExecutable)) {
            $publishedNames = ($publishedFiles.Name -join ", ")
            throw "Expected exactly one published executable for $runtime, but found: $publishedNames"
        }

        if ($signRequested) {
            $signToolLogPath = Join-Path $logDirectory (
                "signtool-$runtime-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date)
            )

            Write-Host "Signing $runtime with certificate $normalizedThumbprint..."
            Write-Host "SignTool: $resolvedSignToolPath"
            & $resolvedSignToolPath sign `
                /sha1 $normalizedThumbprint `
                /s My `
                /fd SHA256 `
                /tr $TimestampUrl.AbsoluteUri `
                /td SHA256 `
                /debug `
                /v `
                $publishedExecutable 2>&1 |
                Tee-Object -FilePath $signToolLogPath

            if ($LASTEXITCODE -ne 0) {
                throw (
                    "SignTool signing for $runtime failed with exit code $LASTEXITCODE. " +
                    "See '$signToolLogPath'."
                )
            }

            & $resolvedSignToolPath verify /pa /all /v $publishedExecutable
            if ($LASTEXITCODE -ne 0) {
                throw "SignTool verification for $runtime failed with exit code $LASTEXITCODE."
            }

            $signature = Get-AuthenticodeSignature -LiteralPath $publishedExecutable
            if (
                $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.Thumbprint -ne $normalizedThumbprint
            ) {
                throw "The signed $runtime executable does not use certificate '$normalizedThumbprint'."
            }
        }

        New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
        $outputExecutable = Join-Path $resolvedOutputDirectory $executableName
        Move-Item -LiteralPath $publishedExecutable -Destination $outputExecutable -Force

        if ($signRequested) {
            Write-Host "Created signed executable: $outputExecutable"
        }
        else {
            Write-Host "Created executable: $outputExecutable"
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagingDirectory) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
        }
    }
}
