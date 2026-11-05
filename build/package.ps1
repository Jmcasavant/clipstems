#Requires -Version 7.2
<#
.SYNOPSIS
    Builds the Clipstems installer: a self-contained publish of the tray app,
    the pinned LGPL FFmpeg build, packed by Velopack into Setup.exe and
    update packages.

.DESCRIPTION
    Output goes to artifacts/Releases: ClipstemsApp-win-Setup.exe (the
    installer), a portable zip, the full (and, with -DownloadPrevious, delta)
    update packages, and the releases feed that installed copies read.

    The same script runs locally and in CI (.github/workflows). Signing is
    picked up by vpk from the environment when configured: VPK_SIGN_PARAMS
    (signtool.exe arguments) or VPK_AZURE_TRUSTED_SIGN_FILE. Without either,
    the installer is unsigned and Windows SmartScreen warns on first run.

.EXAMPLE
    ./build/package.ps1 -Version 0.3.0
#>
param(
    # SemVer 2, e.g. 0.3.0 or 0.3.0-beta.1. Installed copies update to higher versions only.
    [Parameter(Mandatory)][string]$Version,

    # Fetch the latest GitHub release first so vpk can also build a small delta update from it.
    [switch]$DownloadPrevious,

    # Upload the result as a GitHub release tagged v<Version> (needs GITHUB_TOKEN with contents: write).
    # A version with a pre-release suffix (0.3.0-beta.1) is published as a pre-release, which
    # installed copies ignore.
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

# The installer's identity. It is also the install folder, %LocalAppData%\ClipstemsApp, which
# Velopack replaces on install and deletes on uninstall, so it must not be %LocalAppData%\Clipstems,
# where Clipstems keeps settings, logs and the audio buffer. Never change it after a release:
# installed copies only update from packages with the same id.
$PackId = 'ClipstemsApp'
$RepoUrl = 'https://github.com/Jmcasavant/clipstems'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$releases = Join-Path $artifacts 'Releases'
$cache = Join-Path $PSScriptRoot '.cache'
$appProject = Join-Path $root 'src/Clipstems.App/Clipstems.App.csproj'

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not SemVer 2 (e.g. 0.3.0 or 0.3.0-beta.1)." }

# vpk must be the same version as the Velopack library the app is built with.
$velopack = ([xml](Get-Content $appProject)).Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Velopack' } | Select-Object -First 1
if (-not $velopack) { throw "Clipstems.App.csproj has no Velopack package reference." }
$vpkVersion = $velopack.Version

Step "Publishing Clipstems $Version (self-contained, win-x64)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained `
    -p:Version=$Version -p:DebugType=none --output $publishDir

Step 'Adding FFmpeg'
$ffmpeg = Get-Content (Join-Path $PSScriptRoot 'ffmpeg.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory $cache -Force | Out-Null
$zip = Join-Path $cache (Split-Path $ffmpeg.url -Leaf)
if (-not (Test-Path $zip)) {
    Write-Host "Downloading $($ffmpeg.url)"
    Invoke-WebRequest $ffmpeg.url -OutFile "$zip.part"
    Move-Item "$zip.part" $zip -Force
}
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $ffmpeg.sha256) {
    Remove-Item $zip -Force
    throw "FFmpeg download checksum mismatch: expected $($ffmpeg.sha256), got $actual. The cached copy was deleted."
}
$extract = Join-Path $cache 'ffmpeg-extract'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive $zip $extract
$build = Get-ChildItem $extract -Directory | Select-Object -First 1
$bin = Join-Path $build.FullName 'bin'
$dest = Join-Path $publishDir 'ffmpeg'
New-Item -ItemType Directory $dest | Out-Null
# ffmpeg and ffprobe with their shared libraries; ffplay is not used.
Copy-Item (Join-Path $bin 'ffmpeg.exe'), (Join-Path $bin 'ffprobe.exe') $dest
Copy-Item (Join-Path $bin '*.dll') $dest
Copy-Item (Join-Path $build.FullName 'LICENSE.txt') $dest
Copy-Item (Join-Path $PSScriptRoot 'ffmpeg.json') $dest   # read by Settings > About
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') $publishDir
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publishDir 'LICENSE.txt')

Step "Installing vpk $vpkVersion"
$tools = Join-Path $cache "vpk-$vpkVersion"
if (-not (Test-Path (Join-Path $tools '.store'))) {
    dotnet tool install vpk --version $vpkVersion --tool-path $tools
}
$vpk = Join-Path $tools ($IsWindows ? 'vpk.exe' : 'vpk')

if ($DownloadPrevious) {
    Step 'Downloading the previous release for a delta update'
    $tokenArgs = if ($env:GITHUB_TOKEN) { @('--token', $env:GITHUB_TOKEN) } else { @() }
    & $vpk download github --repoUrl $RepoUrl --outputDir $releases @tokenArgs
}

Step 'Packing with Velopack'
& $vpk pack `
    --packId $PackId `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe 'Clipstems.exe' `
    --packTitle 'Clipstems' `
    --packAuthors 'Clipstems contributors' `
    --icon (Join-Path $root 'assets/Clipstems.ico') `
    --shortcuts 'StartMenuRoot' `
    --outputDir $releases

if ($Publish) {
    if (-not $env:GITHUB_TOKEN) { throw 'Publishing needs GITHUB_TOKEN.' }
    Step "Publishing release v$Version to GitHub"
    $preArgs = if ($Version.Contains('-')) { @('--pre') } else { @() }
    & $vpk upload github --repoUrl $RepoUrl --outputDir $releases --token $env:GITHUB_TOKEN `
        --publish --tag "v$Version" --releaseName "Clipstems $Version" @preArgs
}

Step 'Done'
Get-ChildItem $releases | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
