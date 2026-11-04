#!/usr/bin/env bash
# 卸载 Amber 联机节点 / uninstall the Amber relay node
# 用法 / usage:  sudo bash uninstall.sh      （PORT 和安装时一样 / same PORT as install, default 11010）
set -euo pipefail

PORT="${PORT:-11010}"
SERVICE="amber-node"
SERVICE_USER="amber-node"
INSTALL_DIR="/opt/amber-node"

[ "$(id -u)" -eq 0 ] || { echo "请用 root 运行 / run as root" >&2; exit 1; }

if systemctl list-unit-files "${SERVICE}.service" >/dev/null 2>&1; then
    systemctl disable --now "$SERVICE" >/dev/null 2>&1 || true
fi
rm -f "/etc/systemd/system/${SERVICE}.service"
systemctl daemon-reload
rm -rf "$INSTALL_DIR"
if id "$SERVICE_USER" >/dev/null 2>&1; then userdel "$SERVICE_USER" 2>/dev/null || true; fi

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw delete allow "${PORT}/tcp" >/dev/null 2>&1 || true
    ufw delete allow "${PORT}/udp" >/dev/null 2>&1 || true
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --remove-port="${PORT}/tcp" >/dev/null 2>&1 || true
    firewall-cmd --permanent --remove-port="${PORT}/udp" >/dev/null 2>&1 || true
    firewall-cmd --reload >/dev/null 2>&1 || true
fi

echo "已卸载 / uninstalled. 云服务器安全组里的端口规则请自己删除 / remove the cloud security-group rule yourself."
