#requires -Version 7.0
<#
.SYNOPSIS
Builds and validates a local Thunderstore ZIP. Does not upload or install it.
.EXAMPLE
./Publish-Thunderstore.ps1 -ServerPath 'C:\path\to\server' -IconPath 'C:\path\to\icon.png'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerPath,
    [string]$IconPath = (Join-Path $PSScriptRoot 'docs/images/icon.png'),
    [string]$BepInExCore = '',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/thunderstore'),
    [string]$PackageName = 'HeimKit',
    [string]$WebsiteUrl = 'https://github.com/blancanales/HeimKit',
    [string]$ReadmeBaseUrl = 'https://github.com/blancanales/HeimKit/blob/main',
    [string]$Description = 'Server-side quality-of-life features for Windows Valheim dedicated servers: shared exploration and portal markers, torch upkeep, weather protection, sleep coordination and idle-player raid protection.',
    [string[]]$AdditionalDependencies = @(),
    [switch]$Force
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PackageText([string]$Path) {
    $decoder = [System.Text.UTF8Encoding]::new($false, $true)
    $content = $decoder.GetString([IO.File]::ReadAllBytes($Path))
    if ([string]::IsNullOrWhiteSpace($content)) { throw "Required text file is empty: $Path" }
    return $content
}

$projectPath = Join-Path $PSScriptRoot 'src/HeimKit/HeimKit.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($versionNodes.Count -ne 1) { throw 'Expected exactly one explicit Version in HeimKit.csproj.' }
$version = $versionNodes[0].InnerText.Trim()
$versionPattern = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
if ($version -cnotmatch "^$versionPattern`$") { throw "Thunderstore requires Major.Minor.Patch; project version is '$version'." }
$changelogPath = Join-Path $PSScriptRoot 'CHANGELOG.md'
if (!(Test-Path -LiteralPath $changelogPath -PathType Leaf)) { throw 'Missing CHANGELOG.md. Add release notes before packaging for Thunderstore.' }
$changelog = Assert-PackageText $changelogPath
$releases = [regex]::Matches($changelog, "(?m)^## (?<version>$versionPattern)[ \t]*\r?`$")
if ($releases.Count -eq 0 -or $releases[0].Groups['version'].Value -cne $version -or
    @($releases | Where-Object { $_.Groups['version'].Value -ceq $version }).Count -ne 1) {
    throw 'CHANGELOG.md must start with one release heading matching the project version: ## Major.Minor.Patch.'
}
$notesStart = $releases[0].Index + $releases[0].Length
$notesEnd = if ($releases.Count -gt 1) { $releases[1].Index } else { $changelog.Length }
if ($changelog.Substring($notesStart, $notesEnd - $notesStart) -notmatch '(?m)^-[ \t]+\S') {
    throw 'The current CHANGELOG.md release must contain at least one nonempty change bullet.'
}
$assemblyName = [string]$project.Project.PropertyGroup.AssemblyName
$framework = [string]$project.Project.PropertyGroup.TargetFramework
if (!$assemblyName -or !$framework) { throw 'Expected explicit AssemblyName and TargetFramework in HeimKit.csproj.' }
$pluginSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/HeimKit/Plugin.cs') -Raw
$pluginAttributes = [regex]::Matches($pluginSource, '\[BepInPlugin\(\s*"heimkit"\s*,\s*"HeimKit"\s*,\s*"([^"]+)"\s*\)\]')
if ($pluginAttributes.Count -ne 1 -or $pluginAttributes[0].Groups[1].Value -cne $version) {
    throw 'Plugin.cs BepInPlugin version must match HeimKit.csproj Version (one literal attribute expected).'
}
if (!$pluginSource.Contains("Starting version $version.")) { throw 'Plugin startup version must match the project version.' }
$setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'docs/server-setup.md') -Raw
if ($setup -notmatch ('(?m)^\| HeimKit (?:release baseline|build) \| ' + [regex]::Escape($version) + ' \|\s*$')) {
    throw 'Synchronize the HeimKit release baseline row in docs/server-setup.md before packaging.'
}
# The supported loader documentation is the single source for the dependency pin.
$loaderMatch = [regex]::Match($setup, "(?m)^\| BepInExPack Valheim \| ($versionPattern) \|\s*`$")
if (!$loaderMatch.Success) { throw 'Cannot read the supported BepInExPack Valheim version from server setup.' }
$dependencies = @("denikson-BepInExPack_Valheim-$($loaderMatch.Groups[1].Value)") + @($AdditionalDependencies)
foreach ($dependency in $dependencies) {
    if ($dependency -cnotmatch "^[A-Za-z0-9_]+-[A-Za-z0-9_]+-$versionPattern`$") { throw "Invalid dependency string: $dependency" }
}
if (@($dependencies | Select-Object -Unique).Count -ne $dependencies.Count) { throw 'Duplicate dependency strings are not allowed.' }
if (@($dependencies | Where-Object { $_ -match '^denikson-BepInExPack_Valheim-' }).Count -ne 1) {
    throw 'AdditionalDependencies must not override the supported BepInExPack Valheim dependency.'
}
if ($PackageName -cnotmatch '^[A-Za-z0-9_]{1,128}$') { throw 'PackageName must contain 1-128 letters, digits or underscores.' }
if ([string]::IsNullOrWhiteSpace($Description) -or $Description.Length -gt 250) { throw 'Description must contain 1-250 characters.' }
$website = $null
if ($WebsiteUrl -and (![Uri]::TryCreate($WebsiteUrl, [UriKind]::Absolute, [ref]$website) -or $website.Scheme -notin @('http', 'https'))) {
    throw 'WebsiteUrl must be empty or an absolute HTTP(S) URL.'
}
$readmeBase = $null
if (![Uri]::TryCreate($ReadmeBaseUrl, [UriKind]::Absolute, [ref]$readmeBase) -or $readmeBase.Scheme -notin @('http', 'https')) {
    throw 'ReadmeBaseUrl must be an absolute HTTP(S) URL to the published repository branch or tag.'
}
$iconFile = (Get-Item -LiteralPath $IconPath).FullName
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile($iconFile)
try {
    if ($icon.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid -or $icon.Width -ne 256 -or $icon.Height -ne 256) {
        throw 'IconPath must be a valid PNG image exactly 256x256 pixels.'
    }
} finally { $icon.Dispose() }
$readme = Assert-PackageText (Join-Path $PSScriptRoot 'README.md')
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$zipPath = Join-Path $outputPath "$PackageName-$version.zip"
if ((Test-Path -LiteralPath $zipPath) -and !$Force) { throw "ZIP already exists: $zipPath. Use -Force to replace it." }

# Use the existing build entry point and reference setup; do not copy runtime DLLs.
$buildOptions = @{ ServerPath = $ServerPath }
if ($BepInExCore) { $buildOptions.BepInExCore = $BepInExCore }
& (Join-Path $PSScriptRoot 'build.ps1') @buildOptions
$dllPath = Join-Path $PSScriptRoot "src/HeimKit/bin/Release/$framework/$assemblyName.dll"
if (!(Test-Path -LiteralPath $dllPath -PathType Leaf)) { throw "Missing Release DLL: $dllPath" }

# Read metadata without loading the plugin, Unity or BepInEx into this process.
if (!('HeimKitPackaging.Metadata' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
namespace HeimKitPackaging {
    public static class Metadata {
        public static void Validate(string path, string expected) {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var assembly = reader.GetAssemblyDefinition();
            if (assembly.Version != new Version(expected + ".0"))
                throw new InvalidDataException("Built assembly version differs from project version.");
            int count = 0;
            foreach (var handle in reader.TypeDefinitions) {
                var type = reader.GetTypeDefinition(handle);
                foreach (var attrHandle in type.GetCustomAttributes()) {
                    var attr = reader.GetCustomAttribute(attrHandle);
                    if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
                    var member = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                    if (member.Parent.Kind != HandleKind.TypeReference) continue;
                    var owner = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                    if (reader.GetString(owner.Namespace) != "BepInEx" || reader.GetString(owner.Name) != "BepInPlugin") continue;
                    var blob = reader.GetBlobReader(attr.Value);
                    if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() != "heimkit" ||
                        blob.ReadSerializedString() != "HeimKit" || blob.ReadSerializedString() != expected)
                        throw new InvalidDataException("Built BepInPlugin metadata differs from project version/identity.");
                    count++;
                }
            }
            if (count != 1) throw new InvalidDataException("Expected exactly one BepInPlugin attribute in the built DLL.");
        }
    }
}
'@
}
[HeimKitPackaging.Metadata]::Validate($dllPath, $version)
$manifest = [ordered]@{
    name = $PackageName
    version_number = $version
    website_url = $WebsiteUrl
    description = $Description
    dependencies = @($dependencies)
}
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
# Unique temporary archive; an existing good package is replaced only after validation.
$temporaryZip = Join-Path $outputPath ".$([Guid]::NewGuid().ToString('N')).zip"
$files = [ordered]@{
    'icon.png' = $iconFile
    'CHANGELOG.md' = $changelogPath
    "BepInEx/plugins/HeimKit/$assemblyName.dll" = $dllPath
    'LICENSE' = (Join-Path $PSScriptRoot 'LICENSE')
}
# Ship docs so README relative links work in the ZIP. Use absolute links on the package page.
$repoReadme = $readme
$readme = [regex]::Replace($repoReadme, '\]\((docs/[^)]+|AGENTS\.md|LICENSE)\)', {
    param($match)
    $target = $ReadmeBaseUrl.TrimEnd('/') + '/' + $match.Groups[1].Value
    if ($match.Groups[1].Value -match '\.png$') { $target += '?raw=true' }
    return '](' + $target + ')'
})
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') -Recurse -File | Where-Object { $_.Extension -in @('.md', '.png') }) {
    if ($file.Extension -eq '.md') { $null = Assert-PackageText $file.FullName }
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName).Replace('\', '/')
    $files[$relative] = $file.FullName
}
$files['AGENTS.md'] = Join-Path $PSScriptRoot 'AGENTS.md'
$files['config/heimkit.cfg'] = Join-Path $PSScriptRoot 'config/heimkit.cfg'
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key) | Out-Null
        }
        foreach ($entry in @{ 'README.md' = $readme; 'manifest.json' = ($manifest | ConvertTo-Json -Depth 4) }.GetEnumerator()) {
            $stream = $archive.CreateEntry($entry.Key).Open()
            $writer = [IO.StreamWriter]::new($stream, $utf8)
            try { $writer.Write($entry.Value) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        $expected = @($files.Keys) + @('README.md', 'manifest.json')
        $names = @($archive.Entries | ForEach-Object FullName)
        if ($names.Count -ne $expected.Count -or @(Compare-Object $expected $names -CaseSensitive).Count) { throw 'ZIP entry list does not match the package allowlist.' }
        foreach ($entry in $archive.Entries) {
            if ($entry.Length -eq 0) { throw "Empty ZIP entry: $($entry.FullName)" }
            # Read every entry to catch truncation/corruption before publishing the local result.
            $stream = $entry.Open()
            try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() }
        }
        # Confirm that the archive contains the exact source bytes, including the DLL.
        foreach ($file in $files.GetEnumerator()) {
            $stream = $archive.GetEntry($file.Key).Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $packedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($packedHash -cne (Get-FileHash -LiteralPath $file.Value -Algorithm SHA256).Hash) {
                throw "ZIP content differs from the source: $($file.Key)"
            }
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry('manifest.json').Open(), $utf8)
        try { $packedManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($packedManifest.version_number -cne $version -or $packedManifest.name -cne $PackageName -or
            @(Compare-Object @($packedManifest.dependencies) $dependencies -CaseSensitive).Count) { throw 'ZIP manifest differs from the validated metadata.' }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force:$Force
} finally {
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip }
}
Write-Host "Validated Thunderstore package: $zipPath"
Write-Host 'Local packaging complete. Upload this ZIP manually when ready; no upload or server installation was performed.'
Get-Item -LiteralPath $zipPath
