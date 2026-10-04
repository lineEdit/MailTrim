param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$ScratchRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ($env:OS -ne 'Windows_NT') { throw 'The complete check suite requires Windows and WebView2 Runtime.' }
if (-not $ScratchRoot) {
    $ScratchRoot = Join-Path $repoRoot ('artifacts/checks/' + [guid]::NewGuid().ToString('N'))
}
$ScratchRoot = [System.IO.Path]::GetFullPath($ScratchRoot)
if (Test-Path -LiteralPath $ScratchRoot) { throw 'ScratchRoot must be a new directory. Test data is never overwritten.' }

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Push-Location $repoRoot
try {
    if (-not $NoBuild) {
        Invoke-DotNet -Arguments @('restore', 'MailTrim.sln', '--locked-mode')
        Invoke-DotNet -Arguments @('build', 'MailTrim.sln', '-c', $Configuration, '--no-restore')
    }
    New-Item -ItemType Directory -Path $ScratchRoot | Out-Null
    Invoke-DotNet -Arguments @('run', '--project', 'tests/MailTrim.Tests', '-c', $Configuration, '--no-build')
    Invoke-DotNet -Arguments @('run', '--project', 'tests/MailTrim.UpdateTests', '-c', $Configuration, '--no-build', '--', (Join-Path $ScratchRoot 'updates'))
    Invoke-DotNet -Arguments @('run', '--project', 'tests/MailTrim.Smoke', '-c', $Configuration, '--no-build', '--', (Join-Path $ScratchRoot 'webview'))
    Write-Output "All three check suites passed. Test data: $ScratchRoot"
} finally { Pop-Location }
