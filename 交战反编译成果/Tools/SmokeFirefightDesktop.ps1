param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\FirefightDesktop.exe"),
    [int]$TimeoutSeconds = 12,
    [int]$PostStartDelaySeconds = 2,
    [switch]$KeepAlive
)

$resolvedExePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $resolvedExePath)) {
    throw "FirefightDesktop executable not found: $resolvedExePath"
}

$process = Start-Process -FilePath $resolvedExePath -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$windowHandle = 0
$windowTitle = ""

while ((Get-Date) -lt $deadline) {
    try {
        $liveProcess = Get-Process -Id $process.Id -ErrorAction Stop
        $liveProcess.Refresh()
        if ($liveProcess.HasExited) {
            break
        }

        if ($liveProcess.MainWindowHandle -ne 0) {
            $windowHandle = $liveProcess.MainWindowHandle
            $windowTitle = $liveProcess.MainWindowTitle
            break
        }
    }
    catch {
        break
    }

    Start-Sleep -Milliseconds 250
}

if ($windowHandle -eq 0) {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    Write-Output "STARTED_FAIL"
    Write-Output "EXE=$resolvedExePath"
    exit 1
}

Write-Output "STARTED_OK"
Write-Output "EXE=$resolvedExePath"
Write-Output "PID=$($process.Id)"
Write-Output "MAINWINDOW=$windowHandle"
Write-Output "TITLE=$windowTitle"

if (-not $KeepAlive) {
    Start-Sleep -Seconds $PostStartDelaySeconds
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
