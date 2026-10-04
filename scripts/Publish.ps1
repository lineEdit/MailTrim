param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts' }
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
Push-Location $repoRoot
try {
    [xml]$props = Get-Content Directory.Build.props
    $version = $props.Project.PropertyGroup.Version
    $changelog = Get-Content CHANGELOG.md -Raw -Encoding utf8
    $pattern = '(?ms)^## ' + [regex]::Escape($version) + '\s*\r?\n(.*?)(?=^## |\z)'
    $section = [regex]::Match($changelog, $pattern)
    if (-not $section.Success -or [string]::IsNullOrWhiteSpace($section.Groups[1].Value)) {
        throw "CHANGELOG.md must contain release notes under ## $version"
    }
    $out = Join-Path $OutputRoot "MailTrim-$version-$Runtime"
    if ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath "$out.zip") -or (Test-Path -LiteralPath "$out.zip.sha256")) {
        throw 'Package output already exists. Choose a new -OutputRoot to avoid overwriting an existing distribution.'
    }
    dotnet publish src/MailTrim.App/MailTrim.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:RestoreLockedMode=true -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md,LICENSE,SECURITY.md,THIRD-PARTY-NOTICES.md,CHANGELOG.md,CONTRIBUTING.md,Directory.Build.props,global.json -Destination $out
    $docsOut = Join-Path $out 'docs'
    New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
    Copy-Item docs/*.md -Destination $docsOut
    Copy-Item src/MailTrim.App/packages.lock.json -Destination $out
    $section.Groups[1].Value.Trim() | Set-Content (Join-Path $OutputRoot 'release-notes.md') -Encoding utf8
    $zip = "$out.zip"
    Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding utf8
    Write-Output $zip
} finally { Pop-Location }
