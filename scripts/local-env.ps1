# Dot-source this file before running the workspace-local SDK.
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$localToolsRoot = Join-Path $workspaceRoot '.tools'
foreach ($directory in @('tmp', 'home', 'packages', 'python')) {
    New-Item -ItemType Directory -Path (Join-Path $localToolsRoot $directory) -Force | Out-Null
}
$env:TEMP = Join-Path $localToolsRoot 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $localToolsRoot 'home'
$env:NUGET_PACKAGES = Join-Path $localToolsRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $localToolsRoot 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $localToolsRoot 'nuget-plugins'
$env:NUGET_SCRATCH = Join-Path $localToolsRoot 'tmp/NuGetScratch'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:PYTHONPYCACHEPREFIX = Join-Path $localToolsRoot 'python'
$env:PIP_CACHE_DIR = Join-Path $localToolsRoot 'pip-cache'
$env:XDG_CACHE_HOME = Join-Path $localToolsRoot 'cache'
$env:XDG_CONFIG_HOME = Join-Path $localToolsRoot 'config'
$env:XDG_DATA_HOME = Join-Path $localToolsRoot 'data'
$env:DOTNET_ROOT = Join-Path $localToolsRoot 'dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
