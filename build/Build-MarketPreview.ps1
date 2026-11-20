[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'linux-x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $projectRoot 'src/SkinPreviewExporter/SkinPreviewExporter.csproj'
$buildCache = Join-Path $projectRoot 'artifacts/build/market-preview'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts/publish/market-preview'
}

$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "Choose a new market preview output directory: $destination"
}

$destination = $destination.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$parent = [IO.Path]::GetDirectoryName($destination)
$stagingName = '.market-preview-' + [Guid]::NewGuid().ToString('N')
$stage = Join-Path $parent $stagingName
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $compilerDirectory = Join-Path $stage 'compiler'
    dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true -p:PublishSingleFile=true --artifacts-path $buildCache -o $compilerDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Market preview exporter publish failed with exit code $LASTEXITCODE."
    }

    $previewDirectory = Join-Path $stage 'preview'
    dotnet run --project $project -c Release --artifacts-path $buildCache -- emit-runtime-assets --output $previewDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Market preview runtime generation failed with exit code $LASTEXITCODE."
    }

    [IO.Directory]::Move($stage, $destination)
    Write-Output $destination
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $parent -and [IO.Path]::GetFileName($resolvedStage) -eq $stagingName) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}
