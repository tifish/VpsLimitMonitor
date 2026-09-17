# VpsLimitMonitor

Windows 托盘 VPS 流量监视器：轮询 VPS 面板的流量数据，托盘图标实时显示已用流量百分比，剩余低于阈值时弹出 Toast 报警；会话失效时提醒重新登录（WebView2 登录窗口）。

内置支持 NovixLink、HostYun、CstoneCloud 和 LisaHost。LisaHost 支持服务列表、IP、到期日、已用/总流量及运行状态；首次使用时从托盘菜单打开网站登录。

服务器卡片右键可设置字符串 ID（如 `JP 01`、`香港备用`、`001`）。ID 保存在当前配置目录的 `ServerNumbers/<账号名>.tab`，以 IP 匹配；有 ID 的服务器优先按 ID 文本排序。原有数字编号文件继续有效。

## 安装

```powershell
irm https://raw.githubusercontent.com/tifish/VpsLimitMonitor/main/install.ps1 | iex
```

中国大陆网络请使用镜像地址：

```powershell
irm https://ghfast.top/https://raw.githubusercontent.com/tifish/VpsLimitMonitor/main/install.ps1 | iex
```

安装到 `%LOCALAPPDATA%\Programs\VpsLimitMonitor`，创建开始菜单与开机自启快捷方式并启动。未安装 .NET 10 运行库时会自动提权安装。

## 自动更新

程序默认每天检查一次更新（可在托盘菜单"自动更新"中调整），发现新版本后自动下载替换并重启，用户配置与日志不受影响。

## 卸载

退出程序后删除安装目录与快捷方式即可，不写注册表。
