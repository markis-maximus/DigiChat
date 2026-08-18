[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedPublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$pathPrefix = $resolvedPublishDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
# -Force: without it PowerShell skips Hidden/System files, so a hidden secret in
# the publish tree would pass every rule below and not even reach the file count.
$files = @(Get-ChildItem -LiteralPath $resolvedPublishDirectory -Recurse -File -Force)
$relativePaths = @($files | ForEach-Object {
    $_.FullName.Substring($pathPrefix.Length).Replace('\', '/')
})
$errors = [System.Collections.Generic.List[string]]::new()

$requiredPaths = @(
    "DigiChat.Api.dll",
    "appsettings.json",
    "data/lineages.json",
    "data/layout.json",
    "wwwroot/admin/index.html",
    "wwwroot/overlay/index.html"
)

foreach ($requiredPath in $requiredPaths) {
    if ($relativePaths -inotcontains $requiredPath) {
        $errors.Add("Required public file is missing: $requiredPath")
    }
}

if (@($relativePaths | Where-Object { $_ -like "wwwroot/admin/assets/*.js" }).Count -eq 0) {
    $errors.Add("Admin JavaScript bundle is missing.")
}
if (@($relativePaths | Where-Object { $_ -like "wwwroot/overlay/assets/*.js" }).Count -eq 0) {
    $errors.Add("Overlay JavaScript bundle is missing.")
}

$allowedDataPaths = @("data/lineages.json", "data/layout.json")
foreach ($relativePath in $relativePaths) {
    $fileName = [IO.Path]::GetFileName($relativePath)
    $extension = [IO.Path]::GetExtension($relativePath)

    if ($fileName -ieq "appsettings.Local.json" -or $fileName -ieq "twitch-tokens.json") {
        $errors.Add("Local secret/token file was published: $relativePath")
    }
    if ($extension -iin @(".mdf", ".ldf", ".log")) {
        $errors.Add("Local database/log file was published: $relativePath")
    }
    if ($relativePath -imatch "(^|/)(sprites|sheets)(/|$)") {
        $errors.Add("Copyrighted/raw art directory was published: $relativePath")
    }
    if ($relativePath -ieq "wwwroot/overlay/assets/manifest.json") {
        $errors.Add("Generated sprite manifest was published: $relativePath")
    }
    if ($relativePath -ilike "wwwroot/overlay/assets/*" -and $extension -inotmatch "^\.(js|css)$") {
        $errors.Add("Unexpected overlay asset was published: $relativePath")
    }
    if ($relativePath -ilike "wwwroot/overlay/*" -and $extension -iin @(".txt", ".csv", ".md", ".py")) {
        $errors.Add("Raw asset-package input was published: $relativePath")
    }
    if ($relativePath -ilike "data/*" -and $allowedDataPaths -inotcontains $relativePath) {
        $errors.Add("Unexpected data file was published: $relativePath")
    }
}

if ($errors.Count -gt 0) {
    throw "Public artifact verification failed:`n - $($errors -join "`n - ")"
}

Write-Host "Public artifact verified: $($files.Count) files; required data and frontends present; no local secrets, databases, raw drops, generated manifest, or sprite art."
