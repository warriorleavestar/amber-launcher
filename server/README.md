# Jay 服务器版（jay-server）

在一台电脑上开一个 Minecraft 服务器，**只有用 Jay 启动器、拿到邀请的人才能进**。这台电脑可以是 VPS，也可以是局域网里的电脑（Windows、Linux、macOS 都行）。

它做的事：

- 下载官方 MC 服务端和合适的 Java，服务端**只监听本机**（127.0.0.1），外面直接连不上。
- 在一个端口（默认 47670）上跑 Jay 网关。玩家的 Jay 先和网关握手（加密，并核对设备），通过之后才转给 MC 服务端。
- 不认识的连接（扫描器、没有邀请的人）网关一个字节都不回。
- 默认用离线账号：网关核对“这台设备绑定的用户名”和登录用的用户名是否一致。所有人都有正版账号时，可以打开正版验证。

## 快速开始

```bash
# 1. 初始化：--host 写玩家连的地址
#    VPS：公网 IP 或域名；局域网：这台电脑的局域网 IP（比如 192.168.1.20）
jay-server init --host 203.0.113.10

# 2. 下载服务端和 Java（需要同意 Minecraft EULA：https://aka.ms/MinecraftEULA）
jay-server setup --version latest --memory 2048 --accept-eula

# 3. 启动
jay-server run

# 4. 另开一个窗口，给玩家发邀请（也可以在 run 的窗口里输入 jay invite Steve）
jay-server invite Steve
```

把 `JAYS1-…` 这段邀请码私下发给 Steve。Steve 在 Jay 的「联机 → Jay 服务器」里粘贴、点「加入」。之后每次点「进服」就行。

- 邀请码默认 **24 小时、只能用 1 次**（`--ttl 7d`、`--uses 3` 可以改），绑定一个用户名。
- 每个用户名最多绑定 **2 台设备**。换电脑时，给他发一个新邀请。名额满了就先 `revoke` 一台旧的。
- 防火墙或云服务商的安全组里只需要放行网关端口（TCP 47670）。**不要**放行 25565。

## 管理命令

| 命令 | 作用 |
|---|---|
| `jay-server invite <用户名> [--ttl 24h] [--uses 1]` | 生成邀请码 |
| `jay-server list` | 名单、每台设备的设备码和最近在线时间 |
| `jay-server invites` / `cancel-invite <编号>` | 查看或作废还没用掉的邀请 |
| `jay-server revoke <用户名> [设备码前几位]` | 撤销设备（不写设备码就撤销全部） |
| `jay-server disable <用户名>` / `enable <用户名>` | 暂时停用或恢复 |
| `jay-server remove <用户名>` | 从名单移除 |
| `jay-server online-mode on\|off` | 正版验证（默认 off） |

`run` 的窗口里直接输入 MC 控制台命令（比如 `op Steve`、`say hi`）。以 `jay ` 开头的是上面的管理命令。输入 `stop` 会存盘后关闭。

数据目录默认在程序旁边的 `jay-server-data`。可以用 `--dir` 或环境变量 `JAY_SERVER_DIR` 改。里面有：

- `gate/server.key`：服务器身份，**别外传**。丢了就要给所有人重新发邀请。
- `gate/state.json`：名单和邀请。
- `mc/`：服务端和存档。备份这个目录就行，最好先停服或执行 `save-all`。

## Linux 上作为服务运行

```bash
sudo useradd --system --home /var/lib/jay-server --shell /usr/sbin/nologin jay
sudo mkdir -p /opt/jay-server /var/lib/jay-server
sudo cp jay-server /opt/jay-server/ && sudo chmod +x /opt/jay-server/jay-server
sudo chown jay:jay /var/lib/jay-server

sudo -u jay JAY_SERVER_DIR=/var/lib/jay-server /opt/jay-server/jay-server init --host <地址>
sudo -u jay JAY_SERVER_DIR=/var/lib/jay-server /opt/jay-server/jay-server setup --accept-eula

sudo cp jay-server.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now jay-server
journalctl -u jay-server -f      # 看日志

# 发邀请（服务运行时也可以）
sudo -u jay JAY_SERVER_DIR=/var/lib/jay-server /opt/jay-server/jay-server invite Steve
```

## Windows / 局域网电脑

解压后，在这个文件夹里打开终端，执行上面“快速开始”的命令。第一次运行时，Windows 防火墙可能会询问是否允许 jay-server 访问网络：只在局域网里用就勾“专用网络”。保持 `run` 的窗口开着，服务器就一直在线。

## 能防什么、不能防什么

- 能防：拿不到邀请的人、邀请码被转发后的第二次使用、把启动器拷给别人（设备密钥不在启动器文件里）、冒充服务器（邀请里钉住了服务器身份）、扫描器。
- 不能防：玩家本人把自己的设备密钥从电脑里扒出来交给别人；这台服务器本身被入侵。

设计说明见项目的设计文档（隐私+ / 隐私++ 是之后的阶段）。
