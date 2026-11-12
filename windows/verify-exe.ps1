param(
    [string] $Executable = (Join-Path $PSScriptRoot 'dist/XiaoMoBan-Windows-x64.exe'),
    [switch] $UseEphemeralPorts
)

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('XiaoMoBan-exe-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$env:PENNOTE_BRIDGE_DATA = $root
$env:PENNOTE_ADB = Join-Path $root 'unused-adb.exe'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
if ($UseEphemeralPorts) {
    $type = $assembly.GetType('PenNoteBridge.Receiver', $true)
    $constructor = $type.GetConstructor([Reflection.BindingFlags]'Public,NonPublic,Instance', $null, [type[]] @([string]), $null)
    $receiver = $constructor.Invoke([object[]] @([string] $root))
    try {
        $config = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $config.Add('lanPort', 0)
        $config.Add('usbPort', 0)
        $config.Add('pairingPort', 0)
        $config.Add('discoveryEnabled', $false)
        $config.Add('injectionEnabled', $false)
        $receiver.Start($config)
        $snapshot = $receiver.Snapshot()
        foreach ($key in 'lanAddress','usbAddress','pairingAddress') {
            $endpoint = $snapshot[$key]
            if ($endpoint -notmatch ':(\d+)$' -or [int]$Matches[1] -eq 0) { throw "Listener missing: $key" }
            $client = New-Object System.Net.Sockets.TcpClient
            try { $client.Connect('127.0.0.1', [int]$Matches[1]) } finally { $client.Dispose() }
        }
        if (-not $receiver.Pairing('127.0.0.1')['svg'].StartsWith('<svg')) { throw 'Invalid QR code' }
        $receiver.Stop()
        if ($receiver.Snapshot()['running']) { throw 'Receiver did not stop' }
        Write-Output 'Packaged x64 EXE loaded its embedded DLL. LAN, USB, pairing and stop passed on isolated ports.'
    } finally { $receiver.Dispose() }
    exit 0
}
$type = $assembly.GetType('PenNoteBridge.DesktopHost', $true)
$constructor = $type.GetConstructor([Reflection.BindingFlags]'Public,NonPublic,Instance', $null, [type[]] @([string]), $null)
$instance = $constructor.Invoke([object[]] @([string] $root))
try {
    $stopped = $instance.TrySnapshot()
    if ($stopped['running']['lan'] -or $stopped['running']['usb'] -or $stopped['running']['bluetooth']) {
        throw 'Receiver started before activation'
    }
    if ($stopped['fingerprint'].Length -ne 64 -or $stopped['code'].Length -ne 24) { throw 'Identity missing' }
    $settings = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $settings.Add('lan', $true)
    $settings.Add('usb', $true)
    $settings.Add('bluetooth', $true)
    $settings.Add('inject', $false)
    $request = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $request.Add('config', $settings)
    try {
        $null = $instance.Execute('tablet_start', $request)
    } catch {
        Write-Output ('Bluetooth start returned ' + $_.Exception.GetBaseException().Message)
    }
    $running = $instance.TrySnapshot()
    if (-not $running['running']['lan'] -or -not $running['running']['usb']) { throw 'LAN or USB did not start' }
    foreach ($port in 57211, 57212) {
        $client = New-Object System.Net.Sockets.TcpClient
        try { $client.Connect('127.0.0.1', $port) } finally { $client.Dispose() }
    }
    $qr = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $qr.Add('address', '127.0.0.1')
    $pairing = $instance.Execute('tablet_pairing', $qr)
    $expected = "PNB1|127.0.0.1|$($stopped['fingerprint'])|$($stopped['code'])"
    if ($pairing['payload'] -ne $expected -or -not $pairing['svg'].StartsWith('<svg')) { throw 'Invalid pairing code' }
    $null = $instance.Execute('tablet_reset_test', $null)
    $null = $instance.Execute('tablet_stop', $null)
    if ($instance.TrySnapshot()['running'].Values -contains $true) { throw 'Receiver did not stop' }
    Write-Output "Packaged x64 EXE loaded its embedded DLL. LAN/USB listened, Bluetooth=$($running['running']['bluetooth']), pairing and stop passed."
} finally { $instance.Dispose() }
