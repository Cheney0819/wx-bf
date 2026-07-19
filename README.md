# wx-weflow

## 桌宠独立卸载工具

在 Windows 上执行仓库根目录的 `build-uninstaller.ps1` 后，独立的 `一键卸载.exe` 会输出到 `publish-uninstaller/`。该工具可从任意目录运行：它会定位桌宠安装目录、退出该目录中的后台进程、清理快捷方式与开机启动入口、删除安装文件，并显示最终验证结果。

如需指定安装路径，运行：

```powershell
.\一键卸载.exe --install-dir "C:\Path\To\JunjieeDesktopPet"
```

若检测到多个历史安装目录，窗口会要求选择目标路径。通过 `桌宠安装包` 安装的版本仍应从 Windows 的“应用和功能”或安装目录中的标准 Inno Setup 卸载器启动卸载。
