param(
    [Parameter(Mandatory = $true)][string]$ServerPath,
    [string]$BepInExCore = "",
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ProjectPath = 'src\HeimKit\HeimKit.csproj'
)
$ErrorActionPreference = 'Stop'
$taskProjectPath = if ([System.IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $PSScriptRoot $ProjectPath }
$taskProjectDirectory = Split-Path -Parent $taskProjectPath
$taskBuildArgs = @('build', $taskProjectPath, '-c', $Configuration, "-p:ServerPath=$ServerPath")
if ($BepInExCore) { $taskBuildArgs += "-p:BepInExCore=$BepInExCore" }
& dotnet @taskBuildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Check the server and BepInEx paths above.' }
Write-Host "Built ($Configuration): $taskProjectDirectory\bin\$Configuration\netstandard2.1\HeimKit.dll"
Write-Host 'Copy only this plugin DLL into the dedicated server BepInEx\plugins folder.'
Write-Host "Config: $taskProjectDirectory\bin\$Configuration\netstandard2.1\heimkit.cfg"
Write-Host 'HeimKit creates BepInEx\config\heimkit.cfg with all defaults on first use. The supplied config is optional; do not overwrite existing server settings.'
