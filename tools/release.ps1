<#
.SYNOPSIS
    Publishes the version in Directory.Build.props as a GitHub release that Orbit's in-app updater installs.

.DESCRIPTION
    1. Checks the working tree is clean and the tag v<Version> does not exist yet.
    2. Runs the tests and publishes ONE self-contained win-x64 OrbitFileManager.exe (single-file; runs without .NET).
    3. Zips it as OrbitFileManager-win-x64-<Version>.zip (OrbitFileManager\OrbitFileManager.exe) and writes a .sha256.
       The in-app updater of every version downloads this zip: 1.2.0+ replaces its exe, 1.1.x copies the folder
       over its install (the new exe then removes the old files on its first start).
    4. Tags the commit, pushes branch + tag, and creates the GitHub release with the zip, its .sha256 and the bare
       OrbitFileManager.exe (for people who just want the exe:
       https://github.com/ariapiacere/orbit-file-manager/releases/latest/download/OrbitFileManager.exe).

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

# git, gh and dotnet print progress on stderr; Windows PowerShell 5.1 would treat that as a terminating error
# under 'Stop'. Run them with 'Continue' and judge success by the exit code instead.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'dotnet SDK not found.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) not found. Install it and run "gh auth login".' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> found in Directory.Build.props.' }
$tag = "v$version"
$package = "OrbitFileManager-win-x64-$version.zip"
$exeName = 'OrbitFileManager.exe'

if (git status --porcelain) { throw 'Working tree has uncommitted changes. Commit them before releasing.' }
if (git tag --list $tag) { throw "Tag $tag already exists. Bump <Version> in Directory.Build.props first." }

Write-Host "Testing..." -ForegroundColor Cyan
Invoke-Native 'Tests' { & $dotnet test OrbitFileManager.sln -c Release --nologo }

Write-Host "Publishing $version as a single exe..." -ForegroundColor Cyan
$publishDir = Join-Path $root 'publish\OrbitFileManager'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
# Same settings as the PublishSingleFile property group in OrbitFileManager.csproj, spelled out here on purpose.
Invoke-Native 'Publish' { & $dotnet publish src/OrbitFileManager/OrbitFileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=embedded -o $publishDir --nologo }

# The updater treats a package holding only the exe as single-file; anything else next to it would also be copied
# into the user's folder by 1.1.x updaters. Check before anything is pushed.
$published = @(Get-ChildItem $publishDir -Recurse -Force | Where-Object { -not $_.PSIsContainer })
$exePath = Join-Path $publishDir $exeName
if ($published.Count -ne 1 -or -not (Test-Path $exePath)) {
    throw "Expected only $exeName in $publishDir, found: $(($published | ForEach-Object Name) -join ', ')"
}
$exeVersion = (Get-Item $exePath).VersionInfo.ProductVersion
if (-not $exeVersion -or -not $exeVersion.StartsWith($version)) { throw "Published exe reports version '$exeVersion', expected $version." }
Write-Host ("  {0}: {1:N0} MB, version {2}" -f $exeName, ((Get-Item $exePath).Length / 1MB), $exeVersion)

$artifacts = Join-Path $root 'publish\release'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$zipPath = Join-Path $artifacts $package
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = "$zipPath.sha256"
Set-Content -Path $shaPath -Value "$hash  $package" -Encoding ascii -NoNewline
$exeAsset = Join-Path $artifacts $exeName
Copy-Item -Force $exePath $exeAsset

Write-Host "Tagging $tag and pushing..." -ForegroundColor Cyan
Invoke-Native 'git tag' { git tag -a $tag -m "Orbit File Manager $version" }
Invoke-Native 'git push' { git push origin HEAD }
Invoke-Native 'git push tag' { git push origin $tag }

Write-Host "Creating GitHub release..." -ForegroundColor Cyan
$ghArgs = @('release', 'create', $tag, $zipPath, $shaPath, $exeAsset, '--title', "Orbit File Manager $version")
if ($Notes) {
    # Pass notes through a file: inline strings with quotes break native argument passing in PowerShell 5.1.
    $notesPath = Join-Path $artifacts 'release-notes.md'
    Set-Content -Path $notesPath -Value $Notes -Encoding utf8
    $ghArgs += @('--notes-file', $notesPath)
} else {
    $ghArgs += '--generate-notes'
}
Invoke-Native 'gh release create' { gh @ghArgs }

Write-Host "Released $tag. Running copies of Orbit will offer it on their next start." -ForegroundColor Green
