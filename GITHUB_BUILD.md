# 小白版：不用装环境的打包方法

目标：让 GitHub 自动在 Windows 环境里帮你打包，直接产出可安装和可便携运行的桌宠版本。

## 你要做什么

1. 打开 GitHub，新建一个仓库。
2. 把整个 `wechat-monitor` 项目上传到仓库。
3. 打开仓库的 `Actions` 页面。
4. 点左侧 `Build Windows Desktop Pet`。
5. 点右侧 `Run workflow`。
6. 等它跑完，下载构建产物。
7. 下载产物里的安装包或便携包即可。

## Windows 用户要做什么

对方不需要安装 Python，也不需要安装 .NET SDK。

对方只需要：

1. 解压对应架构的压缩包。
2. 确认 Windows 微信已经登录。
3. 双击 `桌宠助手.exe`，并允许管理员权限提示。

## 注意

- 不要上传 `server/.env`，里面有数据库密码。
- `.gitignore` 已经排除了 `server/.env`、日志、数据库文件、编译产物。
- GitHub Actions 现在只打包桌宠主程序和 `wx_decrypt.exe` 自动解密链路。
- 如果杀毒软件拦截 `资料整理.exe`，需要在 Windows 上手动允许。
