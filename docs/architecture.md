# VideoLab 原生工程

VideoLab.App 使用 C# / XAML、WinUI 3、Fluent 控件与 Mica。VideoLab.Core 负责设置、预设、任务、Cookie、插件及更新；VideoLab.Interop 通过 C ABI 4 调用 videolab_core。所有 scratch 字符串在同一锁内复制，事件在后台读取，结果槽显式释放。

VideoLab.PluginWorker 是独立的自包含 C# 下载进程，负责 yt-dlp 参数执行、进度、结果及取消。VideoLab.Packaging 发布该引擎，并使用 NSec / Ed25519 生成兼容 VDX 1 的签名包。工程不含 Dart 或 Flutter 源码与平台脚手架。TikTokDownloader 引擎、清单和入口已移除。

插件由 Windows Job Object 管理进程树。取消优先于迟到的成功结果；可响应的插件保留已完成输出，超时后终止整个进程树。执行前验证签名、清单与已安装文件，安装和卸载在受限数据目录内串行完成，处理 Windows 短暂映像锁。

旧版设置通过兼容适配器导入，包括工具路径、Cookie、编辑预设与插件配置。旧插件可执行文件及归档不复制到新数据目录，使用新签名的 C# 引擎替代。原数据保持不变。

构建仅需 .NET 与 Visual Studio C++ 工具链。12 组 CTest 和 42 项 C# 回归覆盖真实 FFmpeg、各媒体向导、字幕、水印、图像格式、中文路径、GPU 结果、签名篡改、升级回滚、取消、子进程回收及原生下载引擎执行。界面检查覆盖 12 个页面、主题、紧凑窗口和自包含运行时来源。
