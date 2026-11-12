# 构建小墨板 Windows 版

构建需要 Windows x64、Visual Studio 2022 或其 Build Tools 中的「使用 C++ 的桌面开发」组件（含 MSVC 和 Windows SDK）、64 位 .NET Framework 4.x，以及通过 `rustup` 安装的 Rust MSVC 工具链。0.6.3 使用 `rustc 1.99.1` 构建验证。初次构建时，Cargo 会按锁文件从 crates.io 下载依赖。

打开 **x64 Native Tools Command Prompt for VS 2022**，进入仓库根目录，运行：

```powershell
rustup target add x86_64-pc-windows-msvc
powershell.exe -NoProfile -File .\windows\build.ps1
```

可执行文件 `XiaoMoBan-Windows-x64.exe` 及随附的 `LICENSE`、`THIRD-PARTY-NOTICES.txt` 位于 `windows/dist/`。可执行文件内嵌 Rust 接收器，无需 WebView。USB 连接需要单独安装 Android Platform-Tools，并在桌面端选择其中的 `adb.exe`。[Android Platform-Tools](https://developer.android.com/tools/releases/platform-tools)

## 验证

从同一终端运行接收器测试和可执行文件的回环测试：

```powershell
cargo test --release --locked --manifest-path windows/receiver-core/Cargo.toml --target x86_64-pc-windows-msvc
cargo test --release --locked --manifest-path windows/receiver-host/Cargo.toml --target x86_64-pc-windows-msvc
powershell.exe -NoProfile -File .\windows\verify-exe.ps1 -UseEphemeralPorts
```

回环测试使用本机随机端口，检查内嵌 DLL、LAN/USB 监听、配对二维码和停止接收流程。蓝牙、USB 线连接和平板实际笔输入需实机验证。
