[CmdletBinding()]
param(
    [string]$DesktopPublishDirectory,
    [string]$AgentPublishDirectory,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$DesktopPublishDirectory = if ($DesktopPublishDirectory) { $DesktopPublishDirectory } else { Join-Path $PSScriptRoot '发布\desktop' }
$AgentPublishDirectory = if ($AgentPublishDirectory) { $AgentPublishDirectory } else { Join-Path $PSScriptRoot '发布\agent' }
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$stageDirectory = Join-Path $PSScriptRoot '发布\installer-stage'
$setupScript = Join-Path $PSScriptRoot '安装程序\SkinManager.iss'
$languageFile = Join-Path $PSScriptRoot '安装程序\ChineseSimplified.isl'
$outputDirectory = Join-Path $projectRoot '发布'

foreach ($requiredFile in @(
    (Join-Path $DesktopPublishDirectory 'SkinManager.exe'),
    (Join-Path $AgentPublishDirectory 'SkinManager.Agent.exe')
)) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "Missing publish input: $requiredFile"
    }
}

if (-not $CompilerPath) {
    $bundledCompiler = Join-Path $PSScriptRoot '工具\InnoSetup-6.7.4\ISCC.exe'
    if (Test-Path -LiteralPath $bundledCompiler) {
        $CompilerPath = $bundledCompiler
    } else {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) {
            $CompilerPath = $command.Source
        } else {
            throw 'Inno Setup compiler not found. Supply -CompilerPath or unpack the official compiler to 开发\构建\工具\InnoSetup-6.7.4.'
        }
    }
}

if (-not (Test-Path -LiteralPath $CompilerPath)) {
    throw "Inno Setup compiler not found: $CompilerPath"
}
if (-not (Test-Path -LiteralPath $languageFile)) {
    throw "Simplified Chinese language file not found: $languageFile"
}

$resolvedStage = [IO.Path]::GetFullPath($stageDirectory)
$allowedStage = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '发布'))
if (-not $resolvedStage.StartsWith($allowedStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a staging path outside the build output directory: $resolvedStage"
}

if (Test-Path -LiteralPath $resolvedStage) {
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedStage -Force | Out-Null
Get-ChildItem -LiteralPath $DesktopPublishDirectory -Force | Copy-Item -Destination $resolvedStage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $AgentPublishDirectory 'SkinManager.Agent.exe') -Destination $resolvedStage -Force
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $CompilerPath $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}
