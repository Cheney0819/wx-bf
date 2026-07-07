# WeFlow 解密技术接入评估

## 结论

可以接。

而且这里的核心不是 `WeFlow.exe` 客户端，而是两层可独立调用的原生能力：

1. `wx_key.dll`
   - 负责注入微信进程并获取密钥
2. `wcdb_api.dll`
   - 负责直接打开微信加密库并读取消息/联系人/媒体索引

这意味着我们可以继续保持你现在的目标形态：

- 双击桌宠
- 后台静默运行
- 不弹出 WeFlow 客户端
- 自动解密、自动上传

## 研究对象

研究目录：

- `/Users/jiee/Downloads/WeFlow_Decryption_Tech`

关键文件：

- `key/win32/x64/wx_key.dll`
- `wcdb/win32/x64/wcdb_api.dll`
- `wcdb/win32/x64/WCDB.dll`
- `main.js`
- `wcdbWorker.js`

## 已确认的技术事实

### 1. `wx_key.dll` 不是黑盒客户端专用 DLL

已确认它有明确导出：

- `InitializeHook`
- `PollKeyData`
- `GetImageKey`
- `GetStatusMessage`
- `GetLastErrorMsg`
- `CleanupHook`

这说明它可以被我们自己的宿主程序直接加载，不需要启动 `WeFlow.exe`。

### 2. `wcdb_api.dll` 也不是只能给 Electron 用

已确认它导出了完整的 C 风格 API，包括但不限于：

- `wcdb_init`
- `wcdb_open_account`
- `wcdb_close_account`
- `wcdb_set_my_wxid`
- `wcdb_get_sessions`
- `wcdb_get_messages`
- `wcdb_search_messages`
- `wcdb_get_contact`
- `wcdb_get_voice_data`
- `wcdb_resolve_image_hardlink`
- `wcdb_resolve_video_hardlink_md5`
- `wcdb_cloud_init`
- `wcdb_start_monitor_pipe`

这说明 WeFlow 真正的“读库能力”也可以被我们自己的程序直接接管。

### 3. WeFlow 本体只是把 DLL 包了一层

从 `main.js` / `wcdbWorker.js` 可见：

- `main.js` 用 `koffi` 加载 `wx_key.dll`
- `wcdbWorker.js` 用 `koffi` 加载 `wcdb_api.dll`
- 也就是说，WeFlow 自己没有神秘的独占协议，本质上就是 JS 对 DLL 做了一层封装

因此我们完全可以把这层封装改写成：

- C# P/Invoke
- 或一个无界面的本地桥接程序

## 与你当前项目的关系

你当前项目里真正在线上的自动链路核心是：

- `windows/wx_decrypt.py`

它现在已经做的事情是：

1. 自动识别微信进程
2. 自动识别微信 4.x 数据目录
3. 调 `WeFlowKeyBridge.exe`
4. 调 `WeFlowWcdbBridge.exe`
5. 导出消息
6. 生成 `chatlog_export.json` 和 `decrypt_meta.json`
7. 后续再由桌宠/监控链路上传

也就是说，这一步现在已经替换成了：

- `wx_key.dll + wcdb_api.dll`

## 是否影响你现在的后端部署

不影响。

而且当前实现就是保持现有输出契约不变：

- 继续产出 `chatlog_export.json`
- 继续产出 `decrypt_meta.json`
- 继续让桌宠的上传逻辑读这两个文件

这样变动只发生在 Windows 本地“解密导出”这一层：

- 云服务器
- 监控网页
- 推送接口

都不需要因为这次本地解密链路替换而重写。

## 当前落地方案

## 方案 A：最小改动桥接替换，已采用

做法：

1. 保留 `windows/wx_decrypt.py` 作为总入口
2. 新增一个无界面本地桥接层，专门封装：
   - `wx_key.dll`
   - `wcdb_api.dll`
3. 由桥接层输出和当前 `chatlog.exe` 类似的结构化 JSON
4. `wx_decrypt.py` 只负责：
   - 找目录
   - 调桥接层
   - 生成现有导出文件

采用这个方案的原因：

- 对你现有桌宠链路改动最小
- 对服务端影响最小
- 最容易逐步替换、逐步验证
- 出问题时容易回退

当前已经落地为两个 `C#` 无界面桥接程序：

- `WeFlowKeyBridge.exe`
- `WeFlowWcdbBridge.exe`

## 方案 B：把解密逻辑直接塞进 `WeChatMonitor.cs`

做法：

- 桌宠进程自己直接 P/Invoke `wx_key.dll` 和 `wcdb_api.dll`
- 不再经过 `wx_decrypt.py`

优点：

- 进程更少
- 链路更“纯”

缺点：

- 一次性改动太大
- 调试难度更高
- 出问题时桌宠主进程更容易被拖死

当前阶段不建议第一步就这么做。

## 当前落地进度

### 第一步：替掉“取密钥”

目标：

- 不再依赖 `chatlog-key-override.exe`
- 改为 `wx_key.dll` 直接取当前登录微信密钥

产物：

- 返回 `wxid`
- 返回 `aesKey`
- 返回 `xorKey`
- 可选返回图片密钥

这一层已经完成：

- 不再依赖 `WeFlow.exe`
- 不再依赖 `chatlog-key-override.exe`

### 第二步：替掉“数据库读取”

目标：

- 用 `wcdb_open_account` 打开账号库
- 拉取会话列表和消息
- 输出成当前服务端能吃的 JSON

这一层当前已经先覆盖了基础自动导出链路：

- 会话列表
- 基础消息读取
- 继续产出兼容的 `chatlog_export.json`

后续如果要增强，再继续补：

- 图片媒体实际内容
- 视频
- 语音

### 第三步：保持现有上传链路不变

这样桌宠仍然是：

- 启动即后台跑
- 自动解密
- 自动上传

这一点也已经成立：

- 本地解密引擎从 `chatlog.exe` 换成了 WeFlow 抽取出的原生能力
- 桌宠和服务端仍然沿用原来的导出文件约定

## 多账号问题怎么处理

`PollKeyData` 返回的不是单一账号，而是账号列表。

结合你当前项目里的目录识别逻辑，完全可以做这套匹配：

1. 扫描本机 `xwechat_files` / `db_storage`
2. 找到候选 `wxid`
3. 用 `wx_key.dll` 返回的账号列表做匹配
4. 优先选择最近活跃的 `session.db` / `message_0.db`

所以多微信号不是死局，可以自动判定。

## 媒体能力评估

不是只能读文字。

从导出函数看，WeFlow 这一套已经覆盖了媒体相关能力：

- `GetImageKey`
- `wcdb_get_voice_data`
- `wcdb_resolve_image_hardlink`
- `wcdb_resolve_video_hardlink_md5`
- `wcdb_scan_media_stream`

这说明：

- 图片可做
- 视频可做
- 语音可做

但第一阶段不建议把所有媒体一起重写，否则风险会陡增。

## 风险点

### 1. 这是 Windows 原生链路

你现在在 Mac 上只能做代码接入、静态分析、打包准备，不能完成最终真机验证。

### 2. 运行库依赖

`wx_key.dll` / `wcdb_api.dll` 依赖：

- `MSVCP140.dll`
- `KERNEL32.dll`
- 以及 WCDB 自身依赖

所以 Windows 安装包里要把依赖带全，或者要求系统已有 VC++ 运行时。

### 3. 直接替掉 `chatlog.exe` 不是“零工作量”

真正麻烦的不是“打开库”，而是：

- 把读出来的数据整理成你当前后端需要的统一格式
- 保持事件上报、失败状态、历史消息结构稳定

所以这事可做，但不是一句“换 DLL”就结束。

## 当前最合理的工程决策

当前工程实际上已经按下面这条路线推进：

1. 保留你现有后端和上传协议
2. 保留 `wx_decrypt.py` 入口层
3. 新增 WeFlow 原生桥接
4. 已替换密钥获取
5. 已替换基础消息导出
6. 运行链路里已经不再依赖 `chatlog.exe`

这条路线最稳，也最符合你“打开桌宠就全自动后台跑”的目标。

## 我对当前可行性的判断

结论很明确：

- `WeFlow` 完全可以只取它的解密技术，不用它的客户端
- 它完全可以接进你现有项目
- 它不需要改你云端后端的整体部署方式
- 但第一步应该做“桥接替换”，不是直接把所有逻辑塞进桌宠主进程

## 下一步建议

下一步更值得做的是：

1. 在 Windows 真机上验证完整 GitHub 打包产物
2. 继续补齐媒体能力和错误事件可视化
3. 视情况再清理少量残留文档和变量命名

当前这一步已经算真正完成“运行链路去 `chatlog.exe` 化”了。
