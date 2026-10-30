# Visual Studio/MSBuild may inherit PowerShell 7's PSModulePath when launching
# Windows PowerShell 5.1. Resolve the modules for the actual host first.
$hostModules = Join-Path $PSHOME 'Modules'
$modulePaths = @($hostModules)
if ($env:ProgramFiles) { $modulePaths += (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules') }
$modulePaths += @($env:PSModulePath -split ';')
$env:PSModulePath = ($modulePaths | Where-Object { $_ } | Select-Object -Unique) -join ';'
