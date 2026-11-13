param(
    [string] $AndroidSdk = $env:ANDROID_HOME,
    [string] $JavaHome = $env:JAVA_HOME,
    [string] $BuildTools,
    [string] $Python = 'python',
    [string] $Output = (Join-Path $PSScriptRoot '.build/XiaoMoBan-0.6.7-dev.apk')
)

$ErrorActionPreference = 'Stop'
if (!$AndroidSdk -or !$JavaHome) {
    throw 'Set ANDROID_HOME and JAVA_HOME, or pass -AndroidSdk and -JavaHome.'
}
$AndroidSdk = (Resolve-Path -LiteralPath $AndroidSdk).Path
$JavaHome = (Resolve-Path -LiteralPath $JavaHome).Path
if (!$BuildTools) { $BuildTools = Join-Path $AndroidSdk 'build-tools/35.0.0' }
$BuildTools = (Resolve-Path -LiteralPath $BuildTools).Path
$androidJar = (Resolve-Path -LiteralPath (Join-Path $AndroidSdk 'platforms/android-35/android.jar')).Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$javaBin = Join-Path $JavaHome 'bin'
$java = Join-Path $javaBin 'java.exe'
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

function Invoke-Checked {
    param([string] $Program, [string[]] $Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Build tool failed ($LASTEXITCODE): $Program"
    }
}

$dependencyNames = @(Invoke-Checked $Python @((Join-Path $PSScriptRoot 'fetch-dependencies.py')))
$dependencyCache = Join-Path $PSScriptRoot '.deps'
$dependencies = @($dependencyNames | ForEach-Object { Join-Path $dependencyCache $_ })
$build = Join-Path $PSScriptRoot '.build'
$scratch = Join-Path $build ([Guid]::NewGuid().ToString('N'))
$classes = Join-Path $scratch 'classes'
$dex = Join-Path $scratch 'dex'
New-Item -ItemType Directory -Path $classes, $dex, (Split-Path -Parent $Output) -Force | Out-Null
$res = Join-Path $scratch 'res.zip'
$unsigned = Join-Path $scratch 'unsigned.apk'
$aligned = Join-Path $scratch 'aligned.apk'
$jar = Join-Path $scratch 'classes.jar'

Invoke-Checked (Join-Path $BuildTools 'aapt2.exe') @(
    'compile', '--dir', (Join-Path $PSScriptRoot 'res'), '-o', $res
)
Invoke-Checked (Join-Path $BuildTools 'aapt2.exe') @(
    'link', '-I', $androidJar, '--manifest', (Join-Path $PSScriptRoot 'AndroidManifest.xml'),
    '-o', $unsigned, $res
)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -Filter '*.java' |
    Sort-Object FullName | Select-Object -ExpandProperty FullName)
Invoke-Checked (Join-Path $javaBin 'javac.exe') (@(
    '-encoding', 'UTF-8', '--release', '8',
    '-classpath', ((@($androidJar) + $dependencies) -join [IO.Path]::PathSeparator), '-d', $classes
) + $sources)
Invoke-Checked (Join-Path $javaBin 'jar.exe') @('cf', $jar, '-C', $classes, '.')
Invoke-Checked $java (@(
    '-cp', (Join-Path $BuildTools 'lib/d8.jar'), 'com.android.tools.r8.D8',
    '--release', '--min-api', '26', '--lib', $androidJar, '--output', $dex, $jar
) + $dependencies)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$apk = [IO.Compression.ZipFile]::Open($unsigned, [IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($part in Get-ChildItem -LiteralPath $dex -Filter '*.dex') {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $apk, $part.FullName, $part.Name, [IO.Compression.CompressionLevel]::Optimal
        )
    }
    $services = @{}
    foreach ($dependency in $dependencies) {
        $dependencyJar = [IO.Compression.ZipFile]::OpenRead($dependency)
        try {
            foreach ($entry in $dependencyJar.Entries) {
                if ($entry.FullName -notmatch '^META-INF/services/[^/]+$') { continue }
                $reader = [IO.StreamReader]::new($entry.Open())
                try {
                    $services[$entry.FullName] += $reader.ReadToEnd() + [Environment]::NewLine
                } finally {
                    $reader.Dispose()
                }
            }
        } finally {
            $dependencyJar.Dispose()
        }
    }
    foreach ($service in $services.GetEnumerator()) {
        $writer = [IO.StreamWriter]::new($apk.CreateEntry($service.Key).Open())
        try { $writer.Write($service.Value) } finally { $writer.Dispose() }
    }
    foreach ($licenseFile in 'LICENSE', 'THIRD-PARTY-NOTICES.txt') {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $apk, (Join-Path $repoRoot $licenseFile), "META-INF/$licenseFile", [IO.Compression.CompressionLevel]::Optimal
        )
    }
} finally {
    $apk.Dispose()
}
Invoke-Checked (Join-Path $BuildTools 'zipalign.exe') @('-f', '4', $unsigned, $aligned)

$keystore = Join-Path $build 'xiaomoban-local.p12'
if (!(Test-Path -LiteralPath $keystore)) {
    Invoke-Checked (Join-Path $javaBin 'keytool.exe') @(
        '-genkeypair', '-keystore', $keystore, '-storetype', 'PKCS12',
        '-storepass', 'android', '-keypass', 'android', '-alias', 'xiaomoban-local',
        '-dname', 'CN=XiaoMoBan Local Build', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '3650'
    )
}
$signer = Join-Path $BuildTools 'lib/apksigner.jar'
Invoke-Checked $java @(
    '-jar', $signer, 'sign', '--ks', $keystore, '--ks-key-alias', 'xiaomoban-local',
    '--ks-pass', 'pass:android', '--v4-signing-enabled', 'false', '--out', $Output, $aligned
)
Invoke-Checked $java @('-jar', $signer, 'verify', '--verbose', $Output)
Write-Output "APK: $Output"
