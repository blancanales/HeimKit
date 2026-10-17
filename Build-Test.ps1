param(
    [Parameter(Mandatory = $true)][string]$ServerPath,
    [string]$BepInExCore = ''
)

$ErrorActionPreference = 'Stop'

function Invoke-CheckedDotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Test build stopped because a build or automated check failed.' }
}

$taskProjectPath = Join-Path $PSScriptRoot 'src\HeimKit\HeimKit.csproj'
$taskTestsPath = Join-Path $PSScriptRoot 'tests\HeimKit\HeimKit.Tests.csproj'
$taskProject = [xml][IO.File]::ReadAllText($taskProjectPath)
$taskTests = [xml][IO.File]::ReadAllText($taskTestsPath)
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
$taskFramework = [string]$taskProject.Project.PropertyGroup.TargetFramework
$taskTestsFramework = [string]$taskTests.Project.PropertyGroup.TargetFramework
if ($taskVersion -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$') {
    throw 'Test builds require a development version with a work label and counter. Prepare the version before running this script.'
}
if ($taskProject.Project.PropertyGroup.IncludeSourceRevisionInInformationalVersion -ne 'false') {
    throw 'Disable the automatic source-revision suffix before building.'
}
$taskPlugin = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src\HeimKit\Plugin.cs'))
if (!$taskPlugin.Contains('[BepInPlugin("heimkit", "HeimKit", "' + $taskVersion + '")]') -or
    !$taskPlugin.Contains("Starting version $taskVersion. Build configuration: Debug.") -or
    !$taskPlugin.Contains("Starting version $taskVersion. Build configuration: Release.")) {
    throw 'Project, plugin and startup build identities must match before building.'
}
$taskSetup = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'docs\server-setup.md'))
if (!$taskSetup.Contains("| HeimKit development version | $taskVersion (Debug and Release configurations) |")) {
    throw 'The documented development version must match before building.'
}

$taskProperties = @("-p:ServerPath=$ServerPath")
if ($BepInExCore) { $taskProperties += "-p:BepInExCore=$BepInExCore" }

# The project reference builds the Debug plugin and its automated checks together.
Write-Host "Building HeimKit $taskVersion (Debug) and automated checks..."
Invoke-CheckedDotNet -Arguments (@('build', $taskTestsPath, '-c', 'Debug') + $taskProperties)
$taskChecksDll = Join-Path $PSScriptRoot "tests\HeimKit\bin\Debug\$taskTestsFramework\HeimKit.Tests.dll"
Invoke-CheckedDotNet -Arguments @($taskChecksDll)

Write-Host "Building HeimKit $taskVersion (Release)..."
Invoke-CheckedDotNet -Arguments (@('build', $taskProjectPath, '-c', 'Release') + $taskProperties)

$taskOutputRoot = Join-Path $PSScriptRoot "artifacts\builds\HeimKit-$taskVersion"
$taskCopies = @()
foreach ($taskConfiguration in @('Debug', 'Release')) {
    $taskSource = Join-Path $PSScriptRoot "src\HeimKit\bin\$taskConfiguration\$taskFramework"
    $taskDll = Join-Path $taskSource 'HeimKit.dll'
    $taskInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskDll)
    $taskExpectedFileVersion = $taskVersion.Split('-')[0] + '.0'
    if ($taskInfo.ProductVersion -ne $taskVersion -or $taskInfo.FileVersion -ne $taskExpectedFileVersion) {
        throw "Incorrect $taskConfiguration DLL version metadata."
    }
    $taskGeneratedMetadata = Join-Path $PSScriptRoot "src\HeimKit\obj\$taskConfiguration\$taskFramework\HeimKit.AssemblyInfo.cs"
    if (![IO.File]::ReadAllText($taskGeneratedMetadata).Contains('AssemblyConfigurationAttribute("' + $taskConfiguration + '")')) {
        throw "Incorrect generated $taskConfiguration assembly configuration."
    }
    foreach ($taskName in @('HeimKit.dll', 'HeimKit.pdb')) {
        $taskSourceFile = Join-Path $taskSource $taskName
        $taskDestination = Join-Path $taskOutputRoot "$taskConfiguration\$taskName"
        $taskHash = (Get-FileHash -LiteralPath $taskSourceFile -Algorithm SHA256).Hash
        if ((Test-Path -LiteralPath $taskDestination) -and
            (Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash -ne $taskHash) {
            throw "A different artifact already exists for $taskVersion. Assign the next development counter before rebuilding changed code."
        }
        $taskCopies += [pscustomobject]@{ Source = $taskSourceFile; Destination = $taskDestination; Hash = $taskHash }
    }
}

# Expose versioned artifacts only after both configurations and the checks succeeded.
foreach ($taskCopy in $taskCopies) {
    [void][IO.Directory]::CreateDirectory((Split-Path $taskCopy.Destination))
    Copy-Item -LiteralPath $taskCopy.Source -Destination $taskCopy.Destination
    if ((Get-FileHash -LiteralPath $taskCopy.Destination -Algorithm SHA256).Hash -ne $taskCopy.Hash) {
        throw 'Copied test artifact hash mismatch.'
    }
}
Write-Host "Test build completed: $taskVersion"
Write-Host "Debug:   $taskOutputRoot\Debug\HeimKit.dll"
Write-Host "Release: $taskOutputRoot\Release\HeimKit.dll"
Write-Host 'Install only one configuration. Existing server configuration is not changed.'
