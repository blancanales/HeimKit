param(
    [Parameter(Mandatory = $true)][string]$ServerPath,
    [string]$BepInExCore = "",
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ProjectPath = 'src\HeimKit\HeimKit.csproj'
)
$ErrorActionPreference = 'Stop'
$taskProjectPath = if ([System.IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $PSScriptRoot $ProjectPath }
$taskProjectPath = [System.IO.Path]::GetFullPath($taskProjectPath)
if (!(Test-Path -LiteralPath $taskProjectPath -PathType Leaf)) { throw "Project not found: $taskProjectPath" }
$taskProject = [xml][System.IO.File]::ReadAllText($taskProjectPath)
$taskFramework = [string]$taskProject.Project.PropertyGroup.TargetFramework
$taskAssemblyName = [string]$taskProject.Project.PropertyGroup.AssemblyName
if (!$taskAssemblyName) { $taskAssemblyName = [System.IO.Path]::GetFileNameWithoutExtension($taskProjectPath) }
$taskProjectDirectory = Split-Path -Parent $taskProjectPath
$taskBuildArgs = @('build', $taskProjectPath, '-c', $Configuration, "-p:ServerPath=$ServerPath")
if ($BepInExCore) { $taskBuildArgs += "-p:BepInExCore=$BepInExCore" }
& dotnet @taskBuildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Check the server and BepInEx paths above.' }
$taskOutputDirectory = Join-Path $taskProjectDirectory "bin\$Configuration\$taskFramework"
$taskDll = Join-Path $taskOutputDirectory "$taskAssemblyName.dll"
if (!(Test-Path -LiteralPath $taskDll -PathType Leaf)) { throw "Build completed without the expected DLL: $taskDll" }
Write-Host "Built ($Configuration): $taskDll"
Write-Host 'Copy only this plugin DLL into the dedicated server BepInEx\plugins folder.'
Write-Host "Config: $taskOutputDirectory\heimkit.cfg"
Write-Host 'HeimKit creates BepInEx\config\heimkit.cfg with all defaults on first use. The supplied config is optional; do not overwrite existing server settings.'
