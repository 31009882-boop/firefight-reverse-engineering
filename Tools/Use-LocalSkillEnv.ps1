$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetCliHome = Join-Path $repoRoot ".dotnet_cli"
$pythonDeps = Join-Path $repoRoot ".skilldeps"

New-Item -ItemType Directory -Force -Path $dotnetCliHome | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome

if (Test-Path $pythonDeps) {
    $env:PYTHONPATH = if ($env:PYTHONPATH) { "$pythonDeps;$env:PYTHONPATH" } else { $pythonDeps }
}

$ghExe = Get-ChildItem -Path $repoRoot -Recurse -Filter gh.exe -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -like "*\Tools\GitHubCli\bin\gh.exe" -or
        $_.FullName -like "*\Tools\gh-portable\bin\gh.exe" -or
        $_.FullName -like "*\Tools\gh-portable\*\bin\gh.exe"
    } |
    Select-Object -First 1

if ($ghExe) {
    $env:PATH = "$($ghExe.DirectoryName);$env:PATH"
}

Write-Output "DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME"
Write-Output "Use npm.cmd / npx.cmd in PowerShell to bypass script-policy wrappers."
if (Test-Path $pythonDeps) {
    Write-Output "PYTHONPATH includes $pythonDeps"
} else {
    Write-Output "Workspace Python deps folder not present yet: $pythonDeps"
}
if ($ghExe) {
    Write-Output "gh.exe available: $($ghExe.FullName)"
} else {
    Write-Output "gh.exe not yet available in the expected local tool folders."
}
