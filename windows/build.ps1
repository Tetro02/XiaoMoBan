param(
    [string] $ReceiverDll,
    [string] $Output = (Join-Path $PSScriptRoot 'dist/XiaoMoBan-Windows-x64.exe')
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$app = Join-Path $PSScriptRoot 'app'
$manifest = Join-Path $PSScriptRoot 'receiver-host/Cargo.toml'

if (-not $ReceiverDll) {
    $targetDir = Join-Path $PSScriptRoot 'target'
    if ($env:CARGO_TARGET_DIR) {
        $targetDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($env:CARGO_TARGET_DIR)
    }
    $previousFlags = $env:RUSTFLAGS
    try {
        $env:RUSTFLAGS = ("$previousFlags -C target-feature=+crt-static").Trim()
        cargo build --locked --release --manifest-path $manifest --target-dir $targetDir --target x86_64-pc-windows-msvc
        if ($LASTEXITCODE -ne 0) { throw "Rust build failed ($LASTEXITCODE)" }
    } finally { $env:RUSTFLAGS = $previousFlags }
    $ReceiverDll = Join-Path $targetDir 'x86_64-pc-windows-msvc/release/pennote_receiver_host.dll'
}

$receiver = (Resolve-Path -LiteralPath $ReceiverDll).Path
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$outputDirectory = Split-Path -Parent $Output
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw 'The x64 .NET Framework 4.x C# compiler is required.'
}
$sources = (Get-ChildItem -LiteralPath $app -Filter '*.cs' -File).FullName
$args = @(
    '/nologo', '/utf8output', '/codepage:65001', '/langversion:5', '/warnaserror+',
    '/target:winexe', '/platform:x64', '/optimize+', "/out:$Output",
    "/win32manifest:$(Join-Path $app 'app.manifest')",
    "/win32icon:$(Join-Path $app 'icon.ico')",
    "/resource:$receiver,PenNoteBridge.NativeReceiver",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Net.Http.dll'
)
& $csc @args @sources
if ($LASTEXITCODE -ne 0) { throw "C# build failed ($LASTEXITCODE)" }

if ($outputDirectory -ne $repository) {
    foreach ($name in 'LICENSE', 'THIRD-PARTY-NOTICES.txt') {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $outputDirectory
    }
}
