#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('heimkit-packaging-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
$passed = 0

function Expect-Failure([string]$Name, [scriptblock]$Action, [string]$Message) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    if (!$caught -or $caught -notmatch $Message) { throw "$Name failed: expected '$Message'; got '$caught'." }
    $script:passed++
    Write-Host "PASS: $Name"
}
function Write-Text([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
try {
    # Isolate mutated metadata and build failures from the user's checkout.
    foreach ($relative in @('Publish-Thunderstore.ps1', 'build.ps1', 'README.md', 'docs/server-setup.md',
        'docs/images/icon.png', 'src/HeimKit/HeimKit.csproj', 'src/HeimKit/Plugin.cs')) {
        $destination = Join-Path $sandbox $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $destination
    }
    $publish = Join-Path $sandbox 'Publish-Thunderstore.ps1'
    $options = @{ ServerPath = (Join-Path $sandbox 'missing-server') }
    $project = Join-Path $sandbox 'src/HeimKit/HeimKit.csproj'
    $plugin = Join-Path $sandbox 'src/HeimKit/Plugin.cs'
    $setup = Join-Path $sandbox 'docs/server-setup.md'
    $projectText = Get-Content -LiteralPath $project -Raw
    $pluginText = Get-Content -LiteralPath $plugin -Raw
    $setupText = Get-Content -LiteralPath $setup -Raw
    [xml]$xml = $projectText
    $version = [string]$xml.Project.PropertyGroup.Version

    Expect-Failure 'Invalid package name' { & $publish @options -PackageName '../escape' } 'PackageName'
    Expect-Failure 'Overlong description' { & $publish @options -Description ('x' * 251) } 'Description'
    Expect-Failure 'Malformed dependency' { & $publish @options -AdditionalDependencies 'not-a-version' } 'Invalid dependency'
    Expect-Failure 'Conflicting loader dependency' { & $publish @options -AdditionalDependencies 'denikson-BepInExPack_Valheim-1.2.3' } 'must not override'
    Expect-Failure 'Invalid website' { & $publish @options -WebsiteUrl 'file:///private' } 'WebsiteUrl'
    Expect-Failure 'Invalid README base URL' { & $publish @options -ReadmeBaseUrl 'not-a-url' } 'ReadmeBaseUrl'
    Write-Text $project ($projectText.Replace("<Version>$version</Version>", '<Version>1.2.3-beta</Version>'))
    Expect-Failure 'Prerelease project version' { & $publish @options } 'Major.Minor.Patch'
    Write-Text $project $projectText
    Write-Text $plugin ($pluginText.Replace('"' + $version + '"', '"9.9.9"'))
    Expect-Failure 'Plugin source mismatch' { & $publish @options } 'BepInPlugin version'
    Write-Text $plugin $pluginText
    Write-Text $setup ($setupText.Replace("| HeimKit build | $version |", '| HeimKit build | 9.9.9 |'))
    Expect-Failure 'Documented build mismatch' { & $publish @options } 'Synchronize the HeimKit build'
    Write-Text $setup $setupText

    Add-Type -AssemblyName System.Drawing
    $badIcon = Join-Path $sandbox 'wrong-size.png'
    $bitmap = [Drawing.Bitmap]::new(32, 32)
    try { $bitmap.Save($badIcon, [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    Expect-Failure 'Wrong icon dimensions' { & $publish @options -IconPath $badIcon } '256x256'
    $readmePath = Join-Path $sandbox 'README.md'
    [IO.File]::WriteAllBytes($readmePath, [byte[]]@(255, 255, 255))
    Expect-Failure 'Invalid README encoding' { & $publish @options } 'UTF8|Unable to translate|fallback'
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $readmePath -Force

    $output = Join-Path $sandbox 'artifacts/thunderstore'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $zip = Join-Path $output "HeimKit-$version.zip"
    Write-Text $zip 'existing package sentinel'
    $originalHash = (Get-FileHash -LiteralPath $zip).Hash
    Expect-Failure 'Existing ZIP protected' { & $publish @options } 'ZIP already exists'
    Expect-Failure 'Build failure preserves existing ZIP even with Force' { & $publish @options -Force } 'Build failed'
    if ((Get-FileHash -LiteralPath $zip).Hash -ne $originalHash) { throw 'Failed build changed the existing ZIP.' }
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 1) { throw 'Failed packaging left extra output files.' }
    Write-Host "Passed $passed packaging failure checks; existing archive preserved."
} finally {
    # Only the GUID-named test workspace created above may be removed recursively.
    $resolved = [IO.Path]::GetFullPath($sandbox)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ((Split-Path $resolved -Parent) -eq $tempRoot -and (Split-Path $resolved -Leaf) -match '^heimkit-packaging-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { throw 'Refusing to remove a test directory outside the temporary workspace.' }
}
