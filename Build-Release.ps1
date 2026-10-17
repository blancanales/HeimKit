#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ServerPath,
    [string]$BepInExCore = '',
    [string]$IconPath = (Join-Path $PSScriptRoot 'docs/images/icon.png'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/thunderstore'),
    [string]$PackageName = 'HeimKit',
    [string]$ReadmeBaseUrl = '',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

# Git integration and release metadata preparation happen before this local build command.
$taskBranch = & git -C $PSScriptRoot branch --show-current
if ($LASTEXITCODE -ne 0 -or $taskBranch -ne 'main') { throw 'Release builds must run from main after feature integration.' }
$taskChanges = @(& git -C $PSScriptRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $taskChanges.Count -ne 0) { throw 'Commit the reviewed release source and metadata before building.' }
$taskProject = [xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src/HeimKit/HeimKit.csproj'))
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Release builds require a plain Major.Minor.Patch version without a development suffix.'
}
if ($taskProject.Project.PropertyGroup.IncludeSourceRevisionInInformationalVersion -ne 'false') {
    throw 'Disable the automatic source-revision suffix before building.'
}
$taskTestsPath = Join-Path $PSScriptRoot 'tests/HeimKit/HeimKit.Tests.csproj'
$taskTests = [xml][IO.File]::ReadAllText($taskTestsPath)
$taskTestsFramework = [string]$taskTests.Project.PropertyGroup.TargetFramework
$taskBuildArgs = @('build', $taskTestsPath, '-c', 'Debug', "-p:ServerPath=$ServerPath")
if ($BepInExCore) { $taskBuildArgs += "-p:BepInExCore=$BepInExCore" }
& dotnet @taskBuildArgs
if ($LASTEXITCODE -ne 0) { throw 'Release preparation stopped: automated-check build failed.' }
& dotnet (Join-Path $PSScriptRoot "tests/HeimKit/bin/Debug/$taskTestsFramework/HeimKit.Tests.dll")
if ($LASTEXITCODE -ne 0) { throw 'Release preparation stopped: automated checks failed.' }

if (!$ReadmeBaseUrl) { $ReadmeBaseUrl = "https://github.com/blancanales/HeimKit/blob/v$taskVersion" }
$taskPackageArgs = @{
    ServerPath = $ServerPath; IconPath = $IconPath; OutputDirectory = $OutputDirectory
    PackageName = $PackageName; ReadmeBaseUrl = $ReadmeBaseUrl
}
if ($BepInExCore) { $taskPackageArgs.BepInExCore = $BepInExCore }
if ($Force) { $taskPackageArgs.Force = $true }
& (Join-Path $PSScriptRoot 'Publish-Thunderstore.ps1') @taskPackageArgs

$taskFramework = [string]$taskProject.Project.PropertyGroup.TargetFramework
$taskDll = Join-Path $PSScriptRoot "src/HeimKit/bin/Release/$taskFramework/HeimKit.dll"
$taskInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskDll)
if ($taskInfo.ProductVersion -ne $taskVersion -or $taskInfo.FileVersion -ne "$taskVersion.0") {
    throw 'Release DLL file/product versions do not match the intended release.'
}
Write-Host "Release build completed: $taskVersion"
Write-Host "Thunderstore/GitHub ZIP: $(Join-Path $OutputDirectory "$PackageName-$taskVersion.zip")"
Write-Host 'This command does not push Git references, create a GitHub release, delete branches, upload to Thunderstore or install the plugin.'
