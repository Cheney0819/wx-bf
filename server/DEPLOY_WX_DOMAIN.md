# wx.junjiee.online 部署说明

目标：`wx.junjiee.online` 作为统一入口。

```text
Windows 桌宠
  -> https://wx.junjiee.online/api/messages
  -> nginx（反向代理：把域名请求转发到后端）
  -> server.py（后端服务：接收数据、写入 MySQL、展示网页）
  -> MySQL（数据库：保存聊天记录、状态、事件）
```

## 1. 服务器运行后端

把项目放到服务器：

```bash
sudo mkdir -p /opt/wechat-monitor
sudo cp -r wechat-monitor/* /opt/wechat-monitor/
cd /opt/wechat-monitor/server
python3 -m pip install -r requirements.txt
```

编辑 `.env`（环境配置文件）：

```bash
WECHAT_MONITOR_MYSQL_HOST=你的MySQL地址
WECHAT_MONITOR_MYSQL_PORT=3306
WECHAT_MONITOR_MYSQL_USER=root
WECHAT_MONITOR_MYSQL_PASSWORD=你的MySQL密码
WECHAT_MONITOR_MYSQL_DATABASE=wechat_monitor
WECHAT_MONITOR_SERVER_TOKEN=wx_monitor_2026
WECHAT_MONITOR_HEARTBEAT_TIMEOUT_SECONDS=180
```

`WECHAT_MONITOR_SERVER_TOKEN`（上传密钥）必须和桌宠 `monitor_config.json` 里的 `ServerToken` 一样。
`WECHAT_MONITOR_HEARTBEAT_TIMEOUT_SECONDS`（心跳超时时间）默认 180 秒，意思是超过 3 分钟没收到 Windows 端状态就显示离线。

## 2. 配 systemd 后台运行

```bash
sudo cp /opt/wechat-monitor/server/wechat-monitor.service /etc/systemd/system/wechat-monitor.service
sudo systemctl daemon-reload
sudo systemctl enable --now wechat-monitor
sudo systemctl status wechat-monitor
```

`systemd`（Linux 后台服务管理器）会让 `server.py` 开机自启，崩了也自动重启。

## 3. 配 nginx 域名转发

```bash
sudo cp /opt/wechat-monitor/server/nginx.wx.junjiee.online.conf /etc/nginx/conf.d/wx.junjiee.online.conf
sudo nginx -t
sudo systemctl reload nginx
```

现在访问：

```text
https://wx.junjiee.online/
```

应该能看到微信聊天记录查看器。

## 4. 桌宠上传地址

桌宠配置文件：

```text
windows-pet-wpf/monitor_config.json
```

现在已经改成：

```json
{
  "ServerUrl": "https://wx.junjiee.online/api/messages",
  "ServerToken": "wx_monitor_2026",
  "PushIntervalSeconds": 60
}
```

## 5. HTTPS

现在 `https://wx.junjiee.online` 已经能访问，桌宠配置也应该使用 HTTPS。
