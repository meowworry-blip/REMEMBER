# REMEMBER

REMEMBER 是一款 Windows 桌面小组件。它像可收起的背包格子：把截图和文字放进格子，自由拖动、调整大小，并将面板吸附在屏幕边缘。

## 功能

- 从剪贴板粘贴飞书截图或文字，也可导入图片文件。
- 拖动方块排列内容，拖动右下角调整占用的格子；缩放时显示位置虚影。
- 增减网格行列、切换显示器、移动面板并吸附屏幕边缘。
- 5 秒无操作后自动收起；支持通过托盘菜单设置开机自启。

## 下载和使用

在仓库的 **Releases** 页面下载最新的 `REMEMBER-win-x64.zip`，解压到固定位置后运行 `REMEMBER.exe`。详细操作见 [使用说明](使用说明.md)。

图片和文字保存在本机 `%LOCALAPPDATA%\REMEMBER`。发布包不包含这些个人数据。

## 从源码构建

需要 .NET 8 SDK，在 Windows PowerShell 中运行：

```powershell
dotnet publish .\REMEMBER.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o .\publish
```
