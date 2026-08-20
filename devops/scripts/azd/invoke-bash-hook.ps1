[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $ScriptPath,

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]] $ScriptArguments
)

$ErrorActionPreference = "Stop"

$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
$gitBashCandidates = @(
    $(if ($gitCommand) {
        Join-Path (Split-Path (Split-Path $gitCommand.Source -Parent) -Parent) "bin\bash.exe"
    })
    $(if (${env:ProgramFiles}) {
        Join-Path ${env:ProgramFiles} "Git\bin\bash.exe"
    })
    $(if (${env:ProgramFiles(x86)}) {
        Join-Path ${env:ProgramFiles(x86)} "Git\bin\bash.exe"
    })
) | Where-Object { $_ } | Select-Object -Unique

$bashPath = $gitBashCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if (-not $bashPath) {
    throw "Git Bash was not found. Install Git for Windows or add its bin directory to PATH."
}

$resolvedScriptPath = (Resolve-Path -LiteralPath $ScriptPath -ErrorAction Stop).Path

& $bashPath $resolvedScriptPath @ScriptArguments
exit $LASTEXITCODE
