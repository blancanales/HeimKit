param(
    [Parameter(Mandatory = $true)][string]$ServerPath,
    [string]$BepInExCore = "",
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$taskBuildArgs = @('build', (Join-Path $PSScriptRoot 'src\HeimKit\HeimKit.csproj'), '-c', $Configuration, "-p:ServerPath=$ServerPath")
if ($BepInExCore) { $taskBuildArgs += "-p:BepInExCore=$BepInExCore" }
& dotnet @taskBuildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Check the server and BepInEx paths above.' }
Write-Host "Built ($Configuration): $PSScriptRoot\src\HeimKit\bin\$Configuration\netstandard2.1\HeimKit.dll"
Write-Host 'Copy only this plugin DLL into the dedicated server BepInEx\plugins folder.'
Write-Host "Config: $PSScriptRoot\src\HeimKit\bin\$Configuration\netstandard2.1\heimkit.cfg"
Write-Host 'HeimKit creates BepInEx\config\heimkit.cfg with all defaults on first use. The supplied config is optional; do not overwrite existing server settings.'
