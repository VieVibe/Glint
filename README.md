# 轻启 Glint

<img src="src/Glint.App/Assets/glint.svg" width="64" height="64" alt="Glint 图标">

Your apps, a keystroke away.

Windows 应用快捷搜索与文件预览工具。按 `Alt+Space` 搜索应用；在资源管理器或桌面选中文件，按 `Space` 快速预览。

官方便携版免费下载，项目采用 [MIT 许可证](LICENSE)。

## 使用

**[下载 Windows 64 位便携版](https://github.com/VieVibe/Glint/releases/latest/download/Glint-win-x64.zip)** · [全部版本](https://github.com/VieVibe/Glint/releases)

下载 ZIP 并完整解压，运行其中的 `Glint.exe`。软件自带运行所需的 .NET，无需额外安装。复制到其他位置时请保留整个文件夹。首次启动后按 `Alt+Space` 唤出；退出请使用托盘菜单。

- 中文、英文、全拼、首字母和常见别名搜索，例如 `微信 / weixin / wx / WeChat`。
- `↑↓` 选择，`Enter` 启动，`Alt+1` 至 `Alt+8` 直接打开结果。
- 选中结果后，`Ctrl+D` 固定/取消固定，`Ctrl+E` 编辑搜索别名；也可以右键操作。固定应用会标注 `★`。
- 自定义别名支持中文、英文和拼音匹配，使用逗号或换行分隔。编辑窗口中 `Ctrl+Enter` 保存，留空可清除。
- `Esc`、再次按唤出快捷键或点击其他窗口可收起；后台托盘图标可打开或退出。
- 软件在托盘运行时，在资源管理器或桌面选中文件，按 `Space` 打开置顶预览，再按 `Space` 或 `Esc` 关闭，`Enter` 使用默认应用打开。不会接管搜索框、地址栏或重命名输入中的空格。
- 预览支持常见图片、视频、PDF、文字文档，以及 `.docx / .xlsx / .pptx` 的文字内容。PDF 可用 `← →` 翻页；Office 文字预览不保留原始排版和嵌入图片。部分图片和视频格式需要系统提供相应解码器；不支持的格式会显示提示。
- Markdown（`.md`、`.markdown`）默认排版显示标题、列表、引用、表格和代码块，也支持 README 常用的 HTML 居中容器。按 `Ctrl+M` 可切换排版和源码。
- 开发文件可直接预览源码，覆盖 C#、C/C++、Go、Rust、Java、Python、JS/TS、Vue/Svelte，以及 `Dockerfile`、`Makefile`、`.env`、`.gitignore`、项目文件和补丁等。源码视图显示行号，为常见语言和配置提供语法高亮，按 `Ctrl+W` 可切换自动换行。
- 预览中直接滚动鼠标滚轮可放大或缩小，按住 `Shift` 滚动可上下浏览。底部 `−` / `+` 可调节比例，点击百分比可恢复默认大小。
- `Space` 预览默认开启，可从设置或托盘菜单关闭。文字预览最多显示前 20 万字符。
- `F2` 打开设置，可添加/移除便携应用、修改快捷键、选择开机启动；`F5` 刷新索引。
- 快捷键支持 `Alt+Space` 与 `Ctrl+Alt+Space`。注册失败会提示，不会自动抢占其他软件的快捷键。
- 重复运行会唤出已有实例。`--background` 参数可只启动后台常驻。
- `--exit` 可正常退出正在运行的实例，供本地更新流程使用。

首次启动从开始菜单和 Windows 应用列表建立索引。开始菜单变化后会自动合并刷新请求，另有每十分钟一次的补充刷新；刷新不会清空搜索词或重置仍在结果中的选中项。输入时只匹配内存。设置、使用次数、固定应用和别名保存在 `%LOCALAPPDATA%\Glint\settings.json`，兼容旧版设置。开机启动默认关闭，只在用户勾选后写入当前用户的启动项。

## 开发

- C# / .NET 10 / WPF，Windows 10（版本 2004）及以上，x64。
- `src/Glint.App`：Windows 窗口和系统集成。
- `src/Glint.Core`：应用目录、匹配和排序，避免依赖界面框架。
- SDK 精确版本见 `global.json`。

在项目目录中执行：

```powershell
.\scripts\dev.ps1 doctor
.\scripts\dev.ps1 build
.\scripts\dev.ps1 run
.\scripts\dev.ps1 check
.\scripts\dev.ps1 publish
```

生成可分发 ZIP 与 SHA256 校验文件：在已提交的源码目录执行 `.\scripts\package-release.ps1`，产物位于 `artifacts/releases/<版本>/`。版本号来自应用项目的 `Version` 属性。

脚本优先使用系统 SDK，未找到时使用 `%LOCALAPPDATA%\Microsoft\dotnet`，无需管理员权限。也可以在安装了匹配 SDK 的终端中运行 `dotnet build Glint.slnx`。

首次开发前请安装 `global.json` 指定版本的 .NET SDK，安装方式见 [微软 Windows 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。

`check` 执行搜索、排序、持久化、文档预览、预览窗口关闭流程和真实快捷方式索引检查，并输出纯搜索耗时。`publish` 生成自带运行时的 Windows x64 目录。

编译缓存、发布目录、日志与本机配置由 `.gitignore` 排除。图标源文件与生成方法见 [图标说明](src/Glint.App/Assets/README.md)。

当前版本支持应用搜索和 Space 文件预览。图片、PDF、文字和 Office 内容处理均限制资源用量，关闭预览会取消加载并释放文件。错误日志位于 `%LOCALAPPDATA%\Glint\logs`，日志会轮换并限制大小。已知验证边界见 [v0.3.2 发布说明](docs/releases/v0.3.2.md)。拼音采用单字读音，多音字可通过自定义别名补充，部分商店应用先以文字首字显示图标。

产品范围见 [项目说明](docs/project-brief.md)，验证覆盖与后续工作见 [开发状态](docs/development-status.md)。

## 许可证

Copyright (c) 2026 VieVibe。

Glint 的自有代码、文档和图标采用 [MIT 许可证](LICENSE)。允许使用、修改、商用和再分发，包括收费分发。分发软件副本或其实质性部分时须保留版权声明和许可证文本。

第三方组件适用各自的许可证，详见 [第三方声明](THIRD-PARTY-NOTICES.md)。

## 第三方依赖

拼音依赖 [TinyPinyin](https://github.com/hstarorg/TinyPinyin.Net)，PDF 预览使用 Windows Runtime 投影；组件声明见 `THIRD-PARTY-NOTICES.md`。发布目录包含 Windows SDK、C#/WinRT 和 .NET 运行时的相应授权文本。
