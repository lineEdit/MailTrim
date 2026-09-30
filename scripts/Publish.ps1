param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Runtime -ne 'win-x64') { throw 'This release supports win-x64 only' }
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    [xml]$props = Get-Content Directory.Build.props
    $version = $props.Project.PropertyGroup.Version
    $out = Join-Path $repoRoot "artifacts/MailTrim-$version-$Runtime"
    dotnet publish src/MailTrim.App/MailTrim.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md,LICENSE,SECURITY.md,THIRD-PARTY-NOTICES.md -Destination $out
    Copy-Item src/MailTrim.App/packages.lock.json -Destination $out
    $zip = "$out.zip"
    Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding utf8
    Write-Output $zip
} finally { Pop-Location }
