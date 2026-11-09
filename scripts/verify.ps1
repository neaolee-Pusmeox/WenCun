param([switch]$SkipNativeUi)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$props = [xml][IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props 缺少 Version。' }

$readme = [IO.File]::ReadAllText((Join-Path $root 'README.md'))
$expected = '当前预发布版本：`' + $version + '`'
if (-not $readme.Contains($expected)) { throw "README.md 版本与 Directory.Build.props 不一致：$version" }

$changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
if (-not [regex]::IsMatch($changelog, '(?m)^##\s+' + [regex]::Escape($version) + '\s+-')) {
    throw "CHANGELOG.md 缺少 $version 条目。"
}

& $dotnet build (Join-Path $root 'WenCun.slnx')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$tests = Join-Path $root 'tests\WenCun.Tests\WenCun.Tests.csproj'
if ($SkipNativeUi) {
    & $dotnet test $tests --no-build --filter 'FullyQualifiedName!~NativeUiTests&FullyQualifiedName!~ReaderUxTests'
} else {
    & $dotnet test $tests --no-build
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "verify: WenCun $version"
