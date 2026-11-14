# 构建 Android 客户端

当前源码对应小墨板 0.6.8。构建需要 Windows、PowerShell 7、JDK 17、Python 3.9 或更新版本，以及 Android SDK Platform 35 和 Build-Tools 35.0.0。

通过 Android Studio 的 SDK Manager 安装这两个 Android SDK 组件，或使用 [Android SDK 命令行工具](https://developer.android.com/tools/sdkmanager) 安装。

```powershell
sdkmanager.bat "platforms;android-35" "build-tools;35.0.0"
```

在仓库根目录运行。将 `ANDROID_HOME` 和 `JAVA_HOME` 设为你的 SDK 和 JDK 安装目录，也可通过 `-AndroidSdk` 和 `-JavaHome` 指定。

```powershell
.\android\build.ps1
```

首次构建从 Maven Central 下载 `dependencies.lock` 中的依赖并逐个核对 SHA-256。APK 输出到 `android/.build/XiaoMoBan-0.6.8-dev.apk`，包内 `META-INF` 包含项目许可证和第三方许可声明。

构建使用本机新生成的开发签名。如果设备上已安装 GitHub Releases 里的版本，需要先卸载才可安装自行编译的版本。卸载会清除平板端保存的配对和设置。

构建脚本可通过 `-Output` 指定 APK 位置，通过 `-Python` 指定 Python 命令或完整路径。

## 笔身双击四象限

在支持笔身双击和笔尖悬停的华为平板上，开启系统的手写笔双击功能，再到小墨板「笔设置」开启「笔身双击四象限」并保存。平板端和 Windows 端均使用 0.6.8，通过局域网或 USB 连接。

以当前工作区的中心为界。笔尖保持悬空，在左上双击笔身打开小窗白板，右上切换当前白板或批注的橡皮，左下打开屏幕批注，右下无动作。缩放或移动工作区后，分区随工作区移动。若双击没有响应，可以在「笔设置 → 侧键诊断」开始记录，再回工作区悬空双击，查看应用是否收到 `pencil double tap quadrant=` 记录。
