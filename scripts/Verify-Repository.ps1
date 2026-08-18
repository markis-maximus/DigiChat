[CmdletBinding()]
param(
    [switch] $SkipDependencyInstall,
    [switch] $KeepPublish,
    # Directory.Build.props gates TreatWarningsAsErrors, ContinuousIntegrationBuild
    # and RestoreLockedMode on CI=true, so without this a locally green run can
    # still fail GitHub CI on warnings. Opt in for true parity.
    [switch] $StrictCI
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($StrictCI -and -not $env:CI) {
    $env:CI = "true"
    Write-Host "StrictCI: CI=true for this run (warnings are errors)." -ForegroundColor Yellow
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\public-verify"))
$expectedPublishDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\public-verify"))

function Remove-VerificationDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.Equals($expectedPublishDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected path: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string[]] $CommandArguments,
        [Parameter(Mandatory = $true)][string] $WorkingDirectory
    )

    Write-Host "`n==> $Label"
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $Executable @CommandArguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Label failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$node = (Get-Command node.exe -ErrorAction Stop).Source
$npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
if ($null -eq $npmCommand) {
    $npmCommand = Get-Command npm -ErrorAction Stop
}
$npm = $npmCommand.Source
$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source

Invoke-CheckedCommand "Restore locked NuGet graph" $dotnet @("restore", "DigiChat.sln", "--locked-mode") $repositoryRoot

if (-not $SkipDependencyInstall) {
    Invoke-CheckedCommand "Install Overlay dependencies from lockfile" $npm @("ci", "--no-audit", "--no-fund") (Join-Path $repositoryRoot "src\DigiChat.Overlay")
    Invoke-CheckedCommand "Install Admin dependencies from lockfile" $npm @("ci", "--no-audit", "--no-fund") (Join-Path $repositoryRoot "src\DigiChat.Admin")
}

Invoke-CheckedCommand "Audit Overlay dependency graph" $npm @("audit", "--audit-level=low") (Join-Path $repositoryRoot "src\DigiChat.Overlay")
Invoke-CheckedCommand "Audit Admin dependency graph" $npm @("audit", "--audit-level=low") (Join-Path $repositoryRoot "src\DigiChat.Admin")

Invoke-CheckedCommand "Build Overlay frontend" $npm @("run", "build") (Join-Path $repositoryRoot "src\DigiChat.Overlay")
Invoke-CheckedCommand "Build Admin frontend" $npm @("run", "build") (Join-Path $repositoryRoot "src\DigiChat.Admin")
Invoke-CheckedCommand "Validate roster names" $node @("src/DigiChat.Overlay/tools/check-names.mjs") $repositoryRoot
Invoke-CheckedCommand "Build backend and tests" $dotnet @("build", "DigiChat.sln", "--configuration", "Release", "--no-restore") $repositoryRoot
Invoke-CheckedCommand "Run backend tests" $dotnet @("test", "DigiChat.sln", "--configuration", "Release", "--no-build", "--no-restore") $repositoryRoot

Remove-VerificationDirectory -Path $publishDirectory
$verificationSucceeded = $false
try {
    Invoke-CheckedCommand "Publish public-safe artifact" $dotnet @(
        "publish",
        "src/DigiChat.Api/DigiChat.Api.csproj",
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--output", $publishDirectory,
        "-p:SkipFrontendRestore=true"
    ) $repositoryRoot
    Invoke-CheckedCommand "Verify public artifact" $powershell @(
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repositoryRoot "scripts\Test-PublicArtifact.ps1"),
        "-PublishDirectory", $publishDirectory
    ) $repositoryRoot
    $verificationSucceeded = $true
}
finally {
    if ($verificationSucceeded -and -not $KeepPublish) {
        Remove-VerificationDirectory -Path $publishDirectory
    }
}

Write-Host "`nRepository verification passed."
