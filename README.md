# VideoLab

基于 **WinUI 3 + Fluent Design** 的原生 Windows 音视频工作台。使用 C# / XAML 与 C++ 媒体核心，工程与构建不依赖 Flutter 或 Dart。

![VideoLab](docs/screenshots/main.png)

## 功能

- 视频/音频格式转换、音轨提取、压缩、剪切。
- 专业视频与音频向导、命令编辑、导入导出、硬件编码检测及预设。
- 批量图像裁剪、缩放、旋转、PNG/JPEG/WebP/BMP/TIFF，视频封面与截图，即时预览及失败重试。
- 静音、镜像、水印、字幕导出、降噪、音量及响度统一。
- 签名 VDX 扩展中心、配置与依赖检测、安装、更新、回滚、卸载，以及 C# 实现的 yt-dlp 下载引擎。
- 任务队列、取消、结果入口、日志、系统/浅色/深色主题，以及旧版设置与 Cookie 导入。

TikTokDownloader 扩展已移除。yt-dlp 仍可按其支持的站点下载媒体。

## 运行与构建

目标平台为 Windows 10 2004+ / Windows 11 x64。本机已验证 Windows 11；干净 Windows 10 环境实测尚待补充。

下载构建产物请查看 [GitHub Actions](https://github.com/Cydiacoft/VideoLab/actions/workflows/build.yml)。完整解压后运行 `VideoLab.exe`。发布目录包含 .NET、Windows App SDK、媒体核心及签名下载引擎；FFmpeg、ffprobe、yt-dlp 和可选 Aria2 需在设置中配置。

开发需要 .NET SDK 10.0.200、Visual Studio C++ 桌面工具和 Windows SDK。Windows App SDK 2.5.1 通过 NuGet 还原。

```powershell
./tools/build_winui.ps1 -Task Build
$env:FFMPEG_TEST_PATH = 'D:\ffmpeg\bin\ffmpeg.exe'
./tools/build_winui.ps1 -Task Test -SkipBundle
./tools/build_winui.ps1 -Task Smoke -SkipBundle
./tools/build_winui.ps1 -Task Package -SkipBundle
```

便携目录位于 `build/winui-publish/`，ZIP 和 SHA-256 位于 `dist/`。正式构建传入独立的 `VIDEOLAB_PLUGIN_SIGNING_KEY`，并使用 `-Official`；私钥不进入仓库。

## 工程与数据

`src` 包含原生界面、应用服务、C ABI 桥接、C# 下载引擎和签名打包工具；`native/videolab_core` 包含 C++ 核心；`tests` 包含媒体、插件和数据兼容回归。

数据保存在 `%LOCALAPPDATA%\VideoLab\WinUI3`。旧版数据导入保留来源文件；只迁移设置和 Cookie，不复制旧下载引擎。旧数据路径及 VDX 协议标识仅作为兼容接口保留。

项目由 Videoder 的 Windows 原生迁移分离而来，原 Flutter 项目保留在 [Cydiacoft/Videoder](https://github.com/Cydiacoft/Videoder)。许可为 GPL-3.0-only，原始代码的版权与许可声明见 LICENSE 和 NOTICE。
