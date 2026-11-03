<#
.SYNOPSIS
    Publishes the version in Directory.Build.props as a GitHub release that Orbit's in-app updater installs.

.DESCRIPTION
    1. Checks the working tree is clean and the tag v<Version> does not exist yet.
    2. Runs the tests and publishes a self-contained win-x64 build.
    3. Zips it as OrbitFileManager-win-x64-<Version>.zip and writes a .sha256 next to it.
    4. Tags the commit, pushes branch + tag, and creates the GitHub release with both files.

    To ship a new version: bump <Version> in Directory.Build.props, commit, then run
        ./tools/release.ps1 -Notes "What changed"

.PARAMETER Notes
    Release notes shown in Orbit's update window. Defaults to GitHub's generated notes.
#>
param(
    [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'dotnet SDK not found.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) not found. Install it and run "gh auth login".' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> found in Directory.Build.props.' }
$tag = "v$version"
$package = "OrbitFileManager-win-x64-$version.zip"

if (git status --porcelain) { throw 'Working tree has uncommitted changes. Commit them before releasing.' }
if (git tag --list $tag) { throw "Tag $tag already exists. Bump <Version> in Directory.Build.props first." }

Write-Host "Testing..." -ForegroundColor Cyan
& $dotnet test OrbitFileManager.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

Write-Host "Publishing $version..." -ForegroundColor Cyan
$publishDir = Join-Path $root 'publish\OrbitFileManager'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
& $dotnet publish src/OrbitFileManager/OrbitFileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$artifacts = Join-Path $root 'publish\release'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$zipPath = Join-Path $artifacts $package
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = "$zipPath.sha256"
Set-Content -Path $shaPath -Value "$hash  $package" -Encoding ascii -NoNewline

Write-Host "Tagging $tag and pushing..." -ForegroundColor Cyan
git tag -a $tag -m "Orbit File Manager $version"
git push origin HEAD
git push origin $tag

Write-Host "Creating GitHub release..." -ForegroundColor Cyan
$ghArgs = @('release', 'create', $tag, $zipPath, $shaPath, '--title', "Orbit File Manager $version")
if ($Notes) { $ghArgs += @('--notes', $Notes) } else { $ghArgs += '--generate-notes' }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }

Write-Host "Released $tag. Running copies of Orbit will offer it on their next start." -ForegroundColor Green
