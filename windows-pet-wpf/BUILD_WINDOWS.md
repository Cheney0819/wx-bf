# Windows 构建说明

这个目录是最终桌宠工程。目标效果是用户双击 `桌宠助手.exe` 后，桌宠显示，同时后台自动调用辅助组件整理、提取、上传记录。

当前产品策略已经调整为：

1. 桌宠启动即请求管理员权限
2. 后台取密钥、读微信进程、解密链路都默认在管理员权限下运行
3. 首次启动或安装后启动时出现 UAC 提示属于正常行为

## 最省事方案

你如果嫌 `Python 3`、环境变量、`.NET SDK 8` 太麻烦，建议直接用仓库里的 GitHub Actions（自动打包）。

工作流文件：

```text
.github/workflows/build-windows.yml
```

它会在 GitHub 的 Windows 机器上自动：

1. 安装 Python 3。
2. 安装 .NET SDK 8。
3. 执行 `build.ps1`。
4. 分别产出 `DesktopPet-win-x64.zip`、`DesktopPet-win-arm64.zip`。
5. 分别产出 `桌宠安装包-win-x64.exe`、`桌宠安装包-win-arm64.exe`。

这样你本机不用装 `Python 3`，Windows 最终使用的人也不用装。

使用方法：

1. 把仓库推到 GitHub。
2. 打开仓库的 `Actions` 页面。
3. 运行 `Build Windows Desktop Pet`。
4. 下载产物。
5. 如果想直接运行，使用 `DesktopPet.zip`。
6. 如果想要正规安装界面并自选安装路径，使用 `桌宠安装包.exe`。

## 推荐给别人用

优先发：

```text
桌宠安装包-win-arm64.exe
```

如果对方是普通 x64 Windows，再发：

```text
桌宠安装包-win-x64.exe
```

这个安装包支持：

1. 全中文安装界面。
2. 自己选择安装路径。
3. 自动注册系统卸载入口。
4. 可选创建桌面快捷方式。
5. 可选开机自动启动。
6. 安装完成后立即启动。
7. 卸载时自动强制关闭桌宠、`资料整理.exe`、`数据桥.exe`，并清理安装目录。

如果你要给别人一个接近“一键安装”的包，打包完成后把整个 `publish` 文件夹发给对方，让对方在 Windows 上双击：

```text
一键安装.bat
```

它会自动完成：

1. 复制文件到当前用户本机目录。
2. 创建桌面快捷方式。
3. 加入开机自启。
4. 立即启动桌宠。

如果后面要删除桌宠，双击：

```text
一键卸载.bat
```

它会自动：

1. 强制关闭正在运行的桌宠和后台进程。
2. 删除桌面快捷方式。
3. 删除开机自启快捷方式。
4. 删除开始菜单快捷方式。
5. 删除安装目录、缓存目录、解密输出目录（`chat_records`、`chatlog_cache`）。

## 一键打包

在项目根目录执行：

```powershell
.\build.ps1
```

如果你要在 Windows ARM 机器上专门出 ARM64 包，执行：

```powershell
.\build.ps1 -Runtime win-arm64
```

脚本会自动完成：

1. 安装 Python 依赖。
2. 打包 `资料整理.exe`。
3. 复制辅助组件到桌宠工程。
4. 发布 `桌宠助手.exe`。
5. 检查发布目录关键文件。
6. ARM Windows 请使用 `win-arm64` 发布目录。

这条路适合“你自己手里就有 Windows 开发机”的情况。

## 1. 准备环境

在 Windows 开发机安装：

- .NET SDK 8
- Python 3

## 2. 打包解密工具

在仓库根目录执行：

```powershell
cd windows
python -m pip install -U pip
pip install -r requirements.txt
pyinstaller --onefile --name 资料整理 wx_decrypt.py
```

把生成的文件复制到桌宠工程目录：

```powershell
copy .\dist\资料整理.exe ..\windows-pet-wpf\wx_decrypt.exe
```

## 3. 编译桌宠

```powershell
cd windows-pet-wpf
dotnet restore
dotnet publish -c Release -r win-arm64 --self-contained true
```

发布目录会在：

```text
bin/Release/net8.0-windows/win-arm64/publish/
```

如果你自己临时测试，可以直接双击 `桌宠助手.exe`，并允许管理员权限提示。

如果你要正式发给别人安装，推荐双击 `一键安装.bat`。

确认发布目录里至少有：

```text
桌宠助手.exe
一键安装.bat
Support/安装桌宠.ps1
一键卸载.bat
Support/卸载桌宠.ps1
资料整理.exe
Assets/sprite-sheet.png
System.Data.SQLite.dll
SQLite.Interop.dll
.NET 运行时文件
```
