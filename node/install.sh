#!/usr/bin/env bash
# Amber 联机节点一键部署（Linux，x86_64 / arm64，systemd）
# Amber relay node installer (Linux, x86_64 / arm64, systemd)
#
# 下载 EasyTier 官方发布的 easytier-core（固定版本，校验 SHA-256），以普通用户身份作为 systemd 服务运行，
# 只做中继（--no-tun，不建虚拟网卡，不需要 root 权限运行）。
#
# 用法 / usage（以 root 运行 / run as root）:
#   sudo bash install.sh
# 可选环境变量 / optional environment variables:
#   PORT=11010            监听端口（TCP 和 UDP）/ listen port (TCP and UDP)
#   RELAY=amber           amber：只转发 Amber 联机房间（网络名 amber-*）；all：转发所有 EasyTier 网络
#   NODE_NAME=我的节点     分享码里显示的名字 / name shown in the share code
#   PUBLIC_HOST=1.2.3.4   公网 IP 或域名（不填就从本机网卡猜）/ public IP or domain (guessed if empty)
#   BANDWIDTH_LIMIT=0     转发别的网络的限速，字节/秒，0 不限 / relay limit for foreign networks, bytes/s, 0 = none

set -euo pipefail

VERSION="2.6.4"
SHA_X86_64="61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6"
SHA_AARCH64="f533ec25a7ea714e09f645615012200278058525795cc3bb690ff011aec1a70f"

PORT="${PORT:-11010}"
RELAY="${RELAY:-amber}"
NODE_NAME="${NODE_NAME:-}"
PUBLIC_HOST="${PUBLIC_HOST:-}"
BANDWIDTH_LIMIT="${BANDWIDTH_LIMIT:-0}"

INSTALL_DIR="/opt/amber-node"
SERVICE="amber-node"
SERVICE_USER="amber-node"

say() { printf '%s\n' "$*"; }
die() { printf '错误 / error: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "请用 root 运行（sudo bash install.sh）/ run as root"
command -v systemctl >/dev/null 2>&1 || die "需要 systemd / systemd is required"
case "$PORT" in ''|*[!0-9]*) die "PORT 必须是数字 / PORT must be a number" ;; esac
[ "$PORT" -ge 1024 ] && [ "$PORT" -le 65535 ] || die "PORT 要在 1024-65535 之间 / PORT must be 1024-65535"
case "$BANDWIDTH_LIMIT" in ''|*[!0-9]*) die "BANDWIDTH_LIMIT 必须是数字 / must be a number" ;; esac
case "$RELAY" in amber|all) ;; *) die "RELAY 只能是 amber 或 all / RELAY must be amber or all" ;; esac

case "$(uname -m)" in
    x86_64|amd64) ARCH="x86_64"; SHA="$SHA_X86_64" ;;
    aarch64|arm64) ARCH="aarch64"; SHA="$SHA_AARCH64" ;;
    *) die "不支持的架构 / unsupported architecture: $(uname -m)" ;;
esac

need() {
    command -v "$1" >/dev/null 2>&1 && return 0
    say "安装 / installing $1 ..."
    if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq "$1"
    elif command -v dnf >/dev/null 2>&1; then dnf install -y -q "$1"
    elif command -v yum >/dev/null 2>&1; then yum install -y -q "$1"
    elif command -v apk >/dev/null 2>&1; then apk add --quiet "$1"
    else die "找不到 $1，请先手动安装 / please install $1"
    fi
}
need curl
need unzip
command -v sha256sum >/dev/null 2>&1 || die "需要 sha256sum（coreutils）"

ASSET="easytier-linux-${ARCH}-v${VERSION}.zip"
URL="https://github.com/EasyTier/EasyTier/releases/download/v${VERSION}/${ASSET}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

say "下载 / downloading $URL"
curl -fL --retry 3 -o "$TMP/$ASSET" "$URL"
ACTUAL="$(sha256sum "$TMP/$ASSET" | cut -d' ' -f1)"
[ "$ACTUAL" = "$SHA" ] || die "SHA-256 不对，已停止 / checksum mismatch: $ACTUAL"
say "SHA-256 校验通过 / checksum OK"

unzip -q -o "$TMP/$ASSET" -d "$TMP/x"
CORE="$(find "$TMP/x" -type f -name easytier-core | head -n 1)"
CLI="$(find "$TMP/x" -type f -name easytier-cli | head -n 1)"
[ -n "$CORE" ] || die "安装包里没有 easytier-core / easytier-core not found in archive"

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$SERVICE_USER" 2>/dev/null \
        || useradd --system --no-create-home --shell /sbin/nologin "$SERVICE_USER"
fi
mkdir -p "$INSTALL_DIR"
install -m 0755 "$CORE" "$INSTALL_DIR/easytier-core"
[ -n "$CLI" ] && install -m 0755 "$CLI" "$INSTALL_DIR/easytier-cli"

# 中继节点不加入任何房间：随机网络名和密钥只用来给它自己一个身份，房间的内容它解不开。
# The node joins no room: a random network name/secret only gives it an identity; it cannot decrypt rooms.
NODE_NET="amber-node-$(head -c 6 /dev/urandom | od -An -tx1 | tr -d ' \n')"
NODE_SECRET="$(head -c 18 /dev/urandom | od -An -tx1 | tr -d ' \n')"
if [ "$RELAY" = "amber" ]; then WHITELIST="amber-*"; else WHITELIST="*"; fi

ARGS="--network-name ${NODE_NET} --network-secret ${NODE_SECRET} --no-tun"
ARGS="${ARGS} --listeners tcp://0.0.0.0:${PORT} udp://0.0.0.0:${PORT}"
ARGS="${ARGS} --relay-network-whitelist '${WHITELIST}' --hostname amber-node --rpc-portal 127.0.0.1:15888"
[ "$BANDWIDTH_LIMIT" -gt 0 ] && ARGS="${ARGS} --foreign-relay-bps-limit ${BANDWIDTH_LIMIT}"

cat > "/etc/systemd/system/${SERVICE}.service" <<UNIT
[Unit]
Description=Amber relay node (EasyTier ${VERSION})
Documentation=https://github.com/EasyTier/EasyTier
After=network-online.target
Wants=network-online.target

[Service]
User=${SERVICE_USER}
Group=${SERVICE_USER}
ExecStart=/bin/sh -c "exec ${INSTALL_DIR}/easytier-core ${ARGS}"
Restart=always
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictNamespaces=true
LockPersonality=true
MemoryDenyWriteExecute=true
CapabilityBoundingSet=
AmbientCapabilities=
WorkingDirectory=/tmp

[Install]
WantedBy=multi-user.target
UNIT
chmod 0600 "/etc/systemd/system/${SERVICE}.service"

systemctl daemon-reload
systemctl enable --now "$SERVICE" >/dev/null
sleep 2
systemctl is-active --quiet "$SERVICE" || { journalctl -u "$SERVICE" -n 30 --no-pager; die "服务没有启动 / service failed to start"; }

# 本机防火墙（云服务器的安全组要另外在控制台放行）/ local firewall (cloud security groups must be opened separately)
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw allow "${PORT}/tcp" >/dev/null && ufw allow "${PORT}/udp" >/dev/null && say "ufw 已放行 ${PORT}/tcp ${PORT}/udp"
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="${PORT}/tcp" >/dev/null && firewall-cmd --permanent --add-port="${PORT}/udp" >/dev/null && firewall-cmd --reload >/dev/null
    say "firewalld 已放行 ${PORT}/tcp ${PORT}/udp"
fi

if [ -z "$PUBLIC_HOST" ]; then
    PUBLIC_HOST="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") print $(i + 1)}' | head -n 1)"
    case "$PUBLIC_HOST" in
        10.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*|192.168.*|100.6[4-9].*|100.[7-9][0-9].*|100.1[01][0-9].*|100.12[0-7].*|"")
            say ""
            say "注意：本机网卡上是内网地址 ${PUBLIC_HOST:-（未知）}，云服务器一般要用控制台里的公网 IP。"
            say "Note: the interface address is private; use your server's public IP. Re-run with PUBLIC_HOST=<public IP> to print the right share code."
            ;;
    esac
fi

json="{\"v\":1"
[ -n "$NODE_NAME" ] && json="${json},\"name\":\"$(printf '%s' "$NODE_NAME" | sed 's/\\/\\\\/g; s/"/\\"/g')\""
json="${json},\"peers\":[\"tcp://${PUBLIC_HOST}:${PORT}\",\"udp://${PUBLIC_HOST}:${PORT}\"]}"
CODE="AMBNODE-$(printf '%s' "$json" | base64 | tr -d '\n=' | tr '+/' '-_')"

say ""
say "=========================================================="
say "节点已运行 / node is running（${SERVICE}.service）"
say "节点地址 / node address:  tcp://${PUBLIC_HOST}:${PORT}   udp://${PUBLIC_HOST}:${PORT}"
say "转发范围 / relays:        $([ "$RELAY" = amber ] && echo 'Amber 联机房间 only' || echo '所有 EasyTier 网络 / all networks')"
say ""
say "Amber 节点分享码（在联机页“联机节点”里导入）/ Amber node share code:"
say "$CODE"
say ""
say "记得在云服务器安全组放行 TCP 和 UDP ${PORT} / open TCP and UDP ${PORT} in your cloud security group"
say "查看状态 / status:  systemctl status ${SERVICE}"
say "卸载 / uninstall:   sudo bash uninstall.sh"
say "=========================================================="
