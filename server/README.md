# Jay 服务器版（jay-server）

> **实验功能**（v0.7.0 起提供）：已通过自动测试，但还没有连真实 MC 服务端做过完整的端到端测试。遇到问题请到 [Issues](https://github.com/warriorleavestar/jay-launcher/issues/new/choose) 反馈。

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
| `jay-server privacy basic\|plus\|plusplus` | 隐私等级（见下面的隐私+） |
| `jay-server relays add\|remove\|list` | 管理节点 |
| `jay-server relay --origin <网关:端口> [--code JAYR1-…]` | 在节点电脑上运行节点 |
| `jay-server relays code\|revoke <节点>` | 重发节点码 / 吊销节点（隐私++） |
| `jay-server status` | 节点心跳、熔断状态、在线人数 |
| `jay-server rotate on\|off\|status` | 入口端口轮换 |

`run` 的窗口里直接输入 MC 控制台命令（比如 `op Steve`、`say hi`）。以 `jay ` 开头的是上面的管理命令。输入 `stop` 会存盘后关闭。

数据目录默认在程序旁边的 `jay-server-data`。可以用 `--dir` 或环境变量 `JAY_SERVER_DIR` 改。里面有：

- `gate/server.key`：服务器身份，**别外传**。丢了就要给所有人重新发邀请。
- `gate/state.json`：名单和邀请。
- `mc/`：服务端和存档。备份这个目录就行，最好先停服或执行 `save-all`。

## 隐私+（经两个节点进服）

普通模式下，玩家直接连网关，网关（也就是 MC 服务器）的地址玩家都知道。打开隐私+ 之后：

- 玩家的 Jay 随机挑**两个不同的节点**，各走一半：每块数据拆成随机垫片和“数据⊕垫片”两份，分别经两个节点送到网关。只拿到其中一个节点的流量什么也还原不出来；每一路本身也是端到端加密的，节点看不到内容，也改不了。
- 邀请码里写的是节点地址，网关的真实地址不会发给玩家。
- 网关只收节点转来的拆分连接；任何一个节点断开，这条连接就断开，不会退回直连。
- 节点只是一个“原样转发”的小程序，不存任何密钥。VPS、局域网电脑都可以当节点，同一个 `jay-server` 程序就能跑。

```bash
# 在每台节点电脑上（至少两台；局域网里的电脑也行）：
jay-server relay --origin <网关地址>:47670 --listen 47671

# 在网关上登记节点（玩家连的地址）。节点在局域网里、或者和网关在同一台机器上时，
# 用 --source 写网关这边看到的它的 IP：
jay-server relays add 198.51.100.7:47671
jay-server relays add 203.0.113.10:47672 --source 127.0.0.1
jay-server relays list

# 至少两个节点之后：
jay-server privacy plus        # 切回：jay-server privacy basic
```

- 切到隐私+ 之后发的邀请码里是节点地址。已经加入的玩家下次进服时会自动拿到节点列表，改走两路。
- 旧版 Jay（v0.7.0 及更早）进不了隐私+ 服务器，会提示更新。
- 再用防火墙把网关端口（47670）只放行给节点的 IP，源站就完全不对外了。节点上放行它自己的端口（比如 47671）。
- 两路意味着流量翻倍、延迟取两条路里较慢的那条。节点最好离玩家和网关都近。

## 隐私++（节点心跳、熔断、吊销）

在隐私+ 之上再加三件事：

- **心跳**：每个节点用自己的长期密钥和网关保持一条加密认证的心跳通道（节点码 `JAYR1-…` 只用来建立这条通道，网关第一次见到节点时记下它的身份，之后只认这一个）。伪造、重放或换了身份的心跳一律不认。
- **熔断**：发心跳的节点少于两个（默认 15 秒没心跳算掉线）时，网关**断开所有玩家、暂停进服**，并让 MC 服务端 `save-all`（不停服）。节点恢复并持续健康 10 秒后自动解除，玩家在 MC 里重新连接就行，不用重启启动器。三个节点坏一个不会熔断。
- **吊销**：怀疑某个节点不可信时，`relays revoke` 立刻断开所有人，大家重新握手换新密钥，之后玩家拿到的节点列表里不再有它。

```bash
# 加节点时会打印节点码，拿到节点电脑上：
jay-server relays add 198.51.100.7:47671
jay-server relay --origin <网关地址>:47670 --listen 47671 --code JAYR1-...     # 在节点上

jay-server privacy plusplus
jay-server status                      # 节点心跳、熔断状态、在线人数
jay-server relays revoke 198.51.100.7:47671
jay-server relays code 198.51.100.7:47671   # 节点重装或节点码泄露时重发（旧码作废）
```

节点的身份密钥默认在节点程序旁边的 `jay-relay-data/relay.key`（`--dir` 或环境变量 `JAY_RELAY_DIR` 可以改），别外传。

为什么不是“一个节点掉线就关掉全网、让所有人的启动器重启”：那样任何人只要打掉一个节点就能反复踢掉所有人；节点真被人控制时通常也不会掉线。所以掉线用熔断（暂停 + 自动恢复）处理，“不可信”用吊销处理。也没有任何能远程关掉玩家启动器的信号。

## 入口端口轮换（地址簿 + 动态端口）

原理和手机验证器的动态码一样：玩家连的端口每隔一段时间（默认 10 分钟）按种子换一个，范围默认 20000–45000（避开 Windows 的动态端口段，那里常有被系统保留、绑不上的端口；自己指定范围时也建议留在 49152 以下）。没拿到种子的人不知道现在该连哪个端口，扫描器扫不到固定端口，泄露出去的旧地址过一个时间窗就没用了。

- 种子写在邀请码里，入服后网关再下发一份“地址簿”：每个入口（网关直连、每个节点）各有自己的种子，知道一个推不出别的。
- 普通模式下轮换网关的端口；隐私+/++ 下轮换节点的端口（节点要带节点码运行，网关经心跳通道把设置发给它）。网关自己的固定端口留给节点转发，用防火墙只放行节点。
- 从隐私+/++ 降回普通模式时，只要还配着节点，网关的固定端口就一直留给节点转发（别的来源连上来会被直接断开）：之前走两路加入的玩家经节点进一次服，就会拿到网关的轮换种子，之后改走网关的轮换端口。确定不再用节点了，用 `jay-server relays remove` 删掉它们，固定端口才会按轮换设置关掉。
- 普通模式下网关不再把节点地址发给玩家。
- 两边时钟允许差一个时间窗：服务端同时开着上一个、当前、下一个窗的端口。玩家电脑的时间差太多会连不上。
- 已经连着的玩家不受影响，换端口只影响新连接。

```bash
jay-server rotate on [--window 10m] [--range 20000-45000] [--grace 3d]
jay-server rotate status       # 现在每个入口在哪个端口
jay-server rotate off
```

- 防火墙 / 云安全组要放行整个端口范围（TCP）。
- 刚开轮换时固定端口会再开 `--grace`（默认 3 天）：这期间进过服的老玩家会自动拿到种子，之后固定端口关掉，没进过服的老玩家要重新发邀请。
- `--new-seed` 换新种子：所有人保存的轮换信息都会失效，只在种子泄露时用。
- 这只是隐藏入口，**不是加密**。连接的保密仍然靠 Noise 加密握手。

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

设计说明见项目的设计文档。
