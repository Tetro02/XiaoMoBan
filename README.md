# 小墨板 · XiaoMoBan

把带 S Pen 的 Android 平板当作 Windows 笔板。看着电脑写字、批注或打开白板，平板保留独立的输入区。

[下载 0.6.3 首发测试版](https://github.com/Tetro02/XiaoMoBan/releases/tag/v0.6.3)

## 主要功能

- 局域网和 USB 连接，支持笔压、工作区映射及按电脑显示比例缩放。
- 屏幕批注和可缩放的小窗白板。悬空按侧键移动画布，按住侧键落笔可局部擦除。
- 笔移开后可用手指操作触控板，也能从平板发送自定义快捷键。

## 快速开始

1. 在 Windows x64 上运行桌面 EXE，在 Android 平板安装 APK。
2. 使用局域网时，让两台设备连接同一网络。开启电脑端局域网接收，在平板扫码或输入六位配对码。
3. 使用 USB 时，先完成一次小墨板电脑端配对。解压官方 Platform-Tools，把 `platform-tools` 放到 EXE 旁边，或在小墨板的「USB」页选中 `adb.exe`。插入数据线，解锁平板并授权 USB 调试。 [Android Platform-Tools](https://developer.android.google.cn/tools/releases/platform-tools?hl=zh-cn)

## 首发测试状态

实机 USB 测试使用 Galaxy Tab S7+ 和 Windows 11 x64。Windows EXE 为未签名的便携程序，蓝牙 HID 直连仍是测试功能。使用说明、SHA-256 校验值和第三方许可随首发包提供。
