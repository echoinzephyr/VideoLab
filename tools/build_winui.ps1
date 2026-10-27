param(
    [ValidateSet('Build','Test','Publish','Package','Smoke')][string]$Task = 'Build',
    [switch]$Official,
    [switch]$SkipBundle
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'build/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
function Check([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed ($LASTEXITCODE)" } }
Push-Location -LiteralPath $projectRoot
try {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation = (& $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json)[0]
    if (-not $installation) { throw 'Visual Studio C++ desktop tools are required.' }
    $cmake = Join-Path $installation.installationPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    $generator = if ([version]$installation.installationVersion -ge [version]'18.0') { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
    & $cmake -S native/videolab_core -B build/native/winui -G $generator -A x64; Check 'Configure core'
    & $cmake --build build/native/winui --config Release --parallel 6; Check 'Build core'
    if (-not $SkipBundle) {
        $bundleArgs = @('run','--project','src/VideoLab.Packaging/VideoLab.Packaging.csproj','-c','Release','--','bundle')
        if ($Official) { $bundleArgs += '--release' }
        & dotnet @bundleArgs; Check 'Build extension packages'
    }
    if ($Task -eq 'Test') {
        & (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir build/native/winui -C Release --output-on-failure; Check 'Native tests'
        & dotnet test tests/VideoLab.Tests/VideoLab.Tests.csproj -c Release --logger 'trx;LogFileName=winui-tests.trx' --results-directory build/test-results; Check 'C# tests'
    } elseif ($Task -in @('Publish','Package','Smoke')) {
        $publishPath = Join-Path $projectRoot 'build/winui-publish'
        & dotnet publish src/VideoLab.App/VideoLab.App.csproj -c Release -p:Platform=x64 -o $publishPath; Check 'Publish WinUI'
        foreach ($file in @('VideoLab.exe','videolab_core.dll','VideoLab.pri','extensions/index.json','extensions/yt-dlp.vdx','LICENSE','NOTICE')) {
            if (-not (Test-Path -LiteralPath (Join-Path $publishPath $file))) { throw "Incomplete publish: $file" }
        }
        if ($Task -eq 'Smoke') {
            $env:VIDEOLAB_DATA_ROOT = Join-Path $projectRoot ('build/smoke-' + [guid]::NewGuid().ToString('N'))
            $app = Start-Process -FilePath (Join-Path $publishPath 'VideoLab.exe') -ArgumentList '--smoke-test','--capture-ui' -PassThru -WindowStyle Hidden
            if (-not $app.WaitForExit(60000)) { Stop-Process -Id $app.Id; throw 'UI startup timed out.' }
            $reportPath = Join-Path $env:VIDEOLAB_DATA_ROOT 'smoke-test.json'
            if ($app.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) { throw "UI startup failed; inspect $env:VIDEOLAB_DATA_ROOT" }
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            if ($report.pages -lt 12 -or $report.plugins.Count -ne 1 -or -not $report.selfContained) { throw 'Incomplete UI smoke test or runtime loaded outside publish directory.' }
            Write-Output "UI verified: $reportPath"
        }
        if ($Task -eq 'Package') {
            $distPath = Join-Path $projectRoot 'dist'; New-Item -ItemType Directory -Path $distPath -Force | Out-Null
            $archivePath = Join-Path $distPath 'VideoLab-v26.10.27-winui.1+8-windows-x64.zip'
            Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $archivePath -Force
            $digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            Set-Content -LiteralPath "$archivePath.sha256" -Value "$digest  $([IO.Path]::GetFileName($archivePath))"
            Write-Output $archivePath
        }
    } else { & dotnet build src/VideoLab.App/VideoLab.App.csproj -c Release -p:Platform=x64; Check 'Build WinUI' }
} finally { Pop-Location }
