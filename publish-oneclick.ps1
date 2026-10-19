$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$release = Join-Path $dist 'one-click'
$stage = Join-Path $dist ('.publish-' + [guid]::NewGuid().ToString('N'))
$adminTemp = Join-Path $stage 'admin'
$clientTemp = Join-Path $stage 'client'
New-Item -ItemType Directory -Force -Path $release, $adminTemp, $clientTemp | Out-Null
$publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:NuGetAudit=false')
try {
    dotnet publish (Join-Path $root 'ParentControl.Admin\ParentControl.Admin.csproj') @publish -o $adminTemp
    if ($LASTEXITCODE -ne 0) { throw 'Admin publish failed' }
    dotnet publish (Join-Path $root 'ParentControl.ClientApp\ParentControl.ClientApp.csproj') @publish -o $clientTemp
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }
    Copy-Item -LiteralPath (Join-Path $adminTemp 'ParentControl.Admin.exe') -Destination $release -Force
    Copy-Item -LiteralPath (Join-Path $clientTemp 'ParentControl.ClientApp.exe') -Destination $release -Force
    Write-Host "Ready: $release"
}
finally {
    $stagePath = [IO.Path]::GetFullPath($stage)
    $distPath = [IO.Path]::GetFullPath($dist) + [IO.Path]::DirectorySeparatorChar
    if (-not $stagePath.StartsWith($distPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete a staging path outside dist: $stagePath"
    }
    if (Test-Path -LiteralPath $stagePath) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}
