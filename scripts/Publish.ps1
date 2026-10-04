param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [ValidateSet('Both', 'Standalone', 'Lite')][string]$Edition = 'Both',
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
    $editions = if ($Edition -eq 'Both') { @('Standalone', 'Lite') } else { @($Edition) }
    foreach ($currentEdition in $editions) {
        $suffix = if ($currentEdition -eq 'Lite') { '-lite' } else { '' }
        $out = Join-Path $OutputRoot "MailTrim-$version-$Runtime$suffix"
        if ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath "$out.zip") -or (Test-Path -LiteralPath "$out.zip.sha256")) {
            throw 'Package output already exists. Choose a new -OutputRoot to avoid overwriting an existing distribution.'
        }
    }
    foreach ($currentEdition in $editions) {
        $lite = $currentEdition -eq 'Lite'
        $suffix = if ($lite) { '-lite' } else { '' }
        $selfContained = if ($lite) { 'false' } else { 'true' }
        $out = Join-Path $OutputRoot "MailTrim-$version-$Runtime$suffix"
        dotnet publish src/MailTrim.App/MailTrim.App.csproj -c Release -r $Runtime --self-contained $selfContained -p:PublishSingleFile=true -p:DebugType=embedded -p:RestoreLockedMode=true -o $out
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        $licenseOut = Join-Path $out 'licenses'
        New-Item -ItemType Directory -Path $licenseOut -Force | Out-Null
        Copy-Item LICENSE -Destination $licenseOut
        if (-not $lite) {
            $packData = & dotnet msbuild src/MailTrim.App/MailTrim.App.csproj -target:ResolveFrameworkReferences -property:RuntimeIdentifier=$Runtime -property:SelfContained=true -getItem:ResolvedRuntimePack -verbosity:quiet
            if ($LASTEXITCODE -ne 0) { throw 'Runtime license resolution failed' }
            $packs = (($packData -join "`n") | ConvertFrom-Json).Items.ResolvedRuntimePack
            foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
                if (-not ($packs | Where-Object { $_.FrameworkName -eq $framework })) { throw "Missing runtime pack: $framework" }
            }
            foreach ($pack in $packs | Where-Object { $_.FrameworkName -in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') }) {
                $destination = Join-Path $licenseOut "dotnet/$($pack.FrameworkName)"
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                $copied = 0
                foreach ($notice in @('LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT')) {
                    $source = Join-Path $pack.PackageDirectory $notice
                    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $destination; $copied++ }
                }
                if ($copied -eq 0) { throw "Missing license for $($pack.FrameworkName)" }
            }
        }
        $docsOut = Join-Path $out 'docs'
        New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
        $documentMap = @{ 'README.md' = 'README.md'; 'LICENSE' = 'licenses/LICENSE' }
        foreach ($file in @('SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'CONTRIBUTING.md')) { $documentMap[$file] = "docs/$file" }
        foreach ($file in Get-ChildItem docs/*.md) { $documentMap["docs/$($file.Name)"] = "docs/$($file.Name)" }
        foreach ($file in Get-ChildItem rules/*.json) { $documentMap["rules/$($file.Name)"] = "rules/$($file.Name)" }
        foreach ($sourceName in @($documentMap.Keys | Where-Object { $_ -like '*.md' -and $_ -ne 'README.md' })) {
            $sourcePath = Join-Path $repoRoot $sourceName
            $targetPath = Join-Path $out $documentMap[$sourceName]
            $sourceDirectory = Split-Path $sourcePath -Parent
            $targetDirectory = Split-Path $targetPath -Parent
            $content = Get-Content -LiteralPath $sourcePath -Raw -Encoding utf8
            $content = [regex]::Replace($content, '(?<=\]\()([^\s)]+)(?=\))', {
                param($match)
                $link = $match.Value
                if ($link -match '^[a-z]+:|^#') { return $link }
                $parts = $link -split '#', 2
                $resolved = [System.IO.Path]::GetFullPath((Join-Path $sourceDirectory $parts[0]))
                $key = [System.IO.Path]::GetRelativePath($repoRoot, $resolved).Replace('\', '/')
                if ($documentMap.ContainsKey($key)) {
                    $link = [System.IO.Path]::GetRelativePath($targetDirectory, (Join-Path $out $documentMap[$key])).Replace('\', '/')
                } elseif (Test-Path -LiteralPath $resolved) { $link = "https://github.com/lineEdit/MailTrim/blob/main/$key" }
                if ($parts.Count -gt 1) { $link += '#' + $parts[1] }
                return $link
            })
            [System.IO.File]::WriteAllText($targetPath, $content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
        }
        $startGuide = @'
# Запуск MailTrim

Запустите **MailTrim.exe** — это единственный запускаемый файл в этой папке.

MailTrim.dll и MailTrim.runtimeconfig.json нужны для совместимости обновлений. Не удаляйте их.
Папки rules, docs и licenses содержат правила, документацию и лицензии.

[Руководство пользователя](docs/USER-GUIDE.md) · [История изменений](docs/CHANGELOG.md)
[Репозиторий](https://github.com/lineEdit/MailTrim)
'@
        $requirements = if ($lite) {
            "Выпуск **Lite**: перед запуском установите **.NET 10 Desktop Runtime x64** (не обычный .NET Runtime и не только ASP.NET Runtime).`n[Скачать .NET Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)`n"
        } else { "Обычный выпуск: **.NET включён**, отдельно устанавливать его не требуется.`n" }
        $startGuide = $startGuide.Replace('# Запуск MailTrim', "# Запуск MailTrim`n`n$requirements`nОба выпуска требуют Windows 10/11 x64 и [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).")
        [System.IO.File]::WriteAllText((Join-Path $out 'README.md'), $startGuide.Replace("`r`n", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
        $section.Groups[1].Value.Trim() | Set-Content (Join-Path $OutputRoot 'release-notes.md') -Encoding utf8
        $zip = "$out.zip"
        Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding utf8
        Write-Output $zip
    }
} finally { Pop-Location }
