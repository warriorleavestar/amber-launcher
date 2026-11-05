# Amber 联机节点 / Amber relay node

[简体中文](#简体中文) | [English](#english)

## 简体中文

Amber 的联机功能靠 [EasyTier](https://github.com/EasyTier/EasyTier) 组网：房间里的人先连到同一个**联机节点**互相找到对方，能直连就直连，连不上时由节点转发（数据用房间密钥加密，节点看不到内容）。Amber 没有自己的服务器，也不内置节点，所以需要有人搭一个节点，或者用朋友搭好的。

这个文件夹里是一键部署的脚本。节点只做转发，不建虚拟网卡，用普通用户身份运行。

### 方法一：Linux 服务器（推荐）

支持 x86_64 和 arm64，需要 systemd（Ubuntu、Debian、CentOS、Rocky 等大多数发行版都有）。

```bash
curl -fsSLO https://raw.githubusercontent.com/warriorleavestar/jay-launcher/main/node/install.sh
sudo bash install.sh
```

脚本会：

1. 从 EasyTier 的 GitHub 发布页下载固定版本（2.6.4），校验 SHA-256；
2. 建一个专用的系统用户 `amber-node`，注册 systemd 服务 `amber-node`（开机启动，挂了自动重启）；
3. 如果开着 ufw 或 firewalld，放行 TCP 和 UDP 11010；
4. 打印节点地址和 **Amber 节点分享码**，在 Amber 联机页的“联机节点”里导入就能用。

可选设置（写在 `sudo` 后面）：

| 变量 | 默认 | 说明 |
|---|---|---|
| `PORT` | `11010` | 监听端口，TCP 和 UDP 都用这个 |
| `RELAY` | `amber` | `amber` 只转发 Amber 联机房间；`all` 转发所有 EasyTier 网络 |
| `NODE_NAME` | 空 | 分享码里显示的名字 |
| `PUBLIC_HOST` | 自动猜 | 公网 IP 或域名。云服务器网卡上常是内网地址，这时要手动填 |
| `BANDWIDTH_LIMIT` | `0` | 转发别人流量的限速（字节/秒），0 不限 |

例如：`sudo PUBLIC_HOST=203.0.113.5 NODE_NAME=小明的节点 bash install.sh`

卸载：`sudo bash uninstall.sh`

### 方法二：Docker

```bash
curl -fsSLO https://raw.githubusercontent.com/warriorleavestar/jay-launcher/main/node/docker-compose.yml
# 把文件里的 NODE_SECRET 换成一串随机字符
docker compose up -d
```

镜像是 EasyTier 官方的 `easytier/easytier:v2.6.4`。在 Amber 里添加节点 `tcp://你的公网IP:11010`。

### 方法三：Windows

适合一直开着的电脑或 Windows 服务器。用管理员 PowerShell 运行：

```powershell
powershell -ExecutionPolicy Bypass -File install-windows.ps1 -PublicHost 你的公网IP
```

脚本注册一个开机运行的计划任务（用权限最低的 LOCAL SERVICE 账户），在 Windows 防火墙放行端口，打印分享码。卸载加 `-Uninstall`。家里的电脑一般在路由器后面，要在路由器上把 11010 端口（TCP 和 UDP）转发到这台电脑。

### 端口和安全组

- 放行 **TCP 11010** 和 **UDP 11010**（改了 `PORT` 就放行对应端口）。
- 云服务器（阿里云、腾讯云、AWS 等）除了系统防火墙，还要在控制台的**安全组 / 防火墙规则**里放行，脚本改不了这一层。
- 只开这两个端口就够了。管理接口只监听本机 `127.0.0.1:15888`，不要对外开放。

### 带宽

- 能直连的玩家只通过节点“打个招呼”，几乎不占带宽；连不上直连时，游戏数据全部经节点转发。
- Minecraft 联机每个玩家大约 20–100 KB/s，人多、跑图时会更多。1 Mbps 上行大约够两三个人同时转发。
- 按流量计费的服务器建议设 `BANDWIDTH_LIMIT`，或者保持 `RELAY=amber` 只给 Amber 房间用。

### 贡献给更多人用

`RELAY=all` 时节点可以给任何 EasyTier 用户当共享节点。EasyTier 官方文档的[搭建共享节点](https://easytier.cn/guide/network/host-public-server.html)页面讲了公共节点的设置，但目前没有写公开的节点登记流程；想让更多人知道你的节点，可以在 EasyTier 的 [GitHub Issues](https://github.com/EasyTier/EasyTier/issues) 或文档首页列出的社区群组里问。也可以直接把 Amber 节点分享码发给朋友。

## English

Amber's multiplayer uses [EasyTier](https://github.com/EasyTier/EasyTier): everyone in a room first connects to the same **relay node** to find each other, then connects directly when possible and goes through the node when not (traffic is encrypted with the room secret; the node cannot read it). Amber has no servers and ships no nodes, so someone needs to run one, or use a friend's.

This folder has one-step install scripts. The node only relays, creates no network adapter and runs as an unprivileged user.

### Option 1: Linux server (recommended)

x86_64 and arm64, systemd required.

```bash
curl -fsSLO https://raw.githubusercontent.com/warriorleavestar/jay-launcher/main/node/install.sh
sudo bash install.sh
```

The script downloads the pinned EasyTier release (2.6.4) from GitHub and verifies its SHA-256, creates a dedicated `amber-node` system user and an `amber-node` systemd service, opens TCP and UDP 11010 in ufw or firewalld if active, and prints the node address plus an **Amber node share code** you can import on Amber's Multiplayer page.

Options (set them after `sudo`): `PORT` (default 11010), `RELAY` (`amber` = Amber rooms only, `all` = every EasyTier network), `NODE_NAME`, `PUBLIC_HOST` (public IP or domain; set it when the interface only has a private address), `BANDWIDTH_LIMIT` (bytes/s for relayed traffic, 0 = unlimited).

Uninstall: `sudo bash uninstall.sh`

### Option 2: Docker

Download `docker-compose.yml`, replace `NODE_SECRET` with a random string, then `docker compose up -d`. It uses EasyTier's official image `easytier/easytier:v2.6.4`. Add `tcp://<public IP>:11010` in Amber.

### Option 3: Windows

From an elevated PowerShell: `powershell -ExecutionPolicy Bypass -File install-windows.ps1 -PublicHost <public IP>`. It registers a startup scheduled task running as LOCAL SERVICE, adds Windows Firewall rules and prints a share code. Add `-Uninstall` to remove it. Behind a home router, forward TCP and UDP 11010 to the PC.

### Ports and security groups

Open **TCP 11010** and **UDP 11010**. On cloud servers also open them in the provider's **security group / firewall rules**; the script cannot change those. The management port (`127.0.0.1:15888`) listens on localhost only and must not be exposed.

### Bandwidth

Peers that connect directly barely use the node. When they can't, all game traffic is relayed: roughly 20–100 KB/s per Minecraft player, more when exploring. On metered servers set `BANDWIDTH_LIMIT` or keep `RELAY=amber`.

### Sharing your node more widely

With `RELAY=all` the node can serve any EasyTier user. EasyTier's [shared node guide](https://easytier.cn/guide/network/host-public-server.html) covers public node settings but currently documents no public listing process; ask in EasyTier's [GitHub Issues](https://github.com/EasyTier/EasyTier/issues) or the community channels linked from its docs. Or just send your Amber node share code to friends.
