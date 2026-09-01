#!/usr/bin/env bash
# WebDataStudio on a Debian machine: the self-contained build from the latest release, a service
# user, a systemd unit and a generated password.
#
# Runs on its own as well as from webdatastudio.sh, which is what makes it testable by hand -- on a
# plain Debian VM, in an LXC container, on a Raspberry Pi:
#
#   curl -fsSL https://raw.githubusercontent.com/fgilde/WebDataStudio/master/deploy/proxmox/install.sh | bash
#
# No Docker: the release ships a single self-contained binary, and Docker inside an unprivileged LXC
# container starts nothing on a current Proxmox -- runc writes net.ipv4.ip_unprivileged_port_start and
# /proc/sys is read-only there.
#
# Idempotent: run it again and it fetches the current release, keeps the password, the data directory
# and the port it already wrote, and restarts the service.
set -euo pipefail

PORT="${WDS_PORT:-8095}"
DATA_DIR="${WDS_DATA_DIR:-/var/lib/webdatastudio}"
INSTALL_DIR="/opt/webdatastudio"
ENV_FILE="/etc/webdatastudio.env"
REPO="fgilde/WebDataStudio"
ADMIN_USER="${WDS_USER:-admin}"

note() { echo "==> $*"; }
die() { echo "webdatastudio: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root"

case "$(uname -m)" in
  x86_64) ASSET="webdatastudio-linux-x64.tar.gz" ;;
  aarch64 | arm64) ASSET="webdatastudio-linux-arm64.tar.gz" ;;
  *) die "no release build for $(uname -m)" ;;
esac

export DEBIAN_FRONTEND=noninteractive
note "packages"
apt-get update -qq
apt-get install -y -qq --no-install-recommends curl ca-certificates tar openssl >/dev/null
# .NET wants ICU for anything culture-aware, and Debian keeps renaming the package with each release.
apt-get install -y -qq --no-install-recommends libicu76 >/dev/null 2>&1 ||
  apt-get install -y -qq --no-install-recommends libicu72 >/dev/null 2>&1 ||
  apt-get install -y -qq --no-install-recommends libicu-dev >/dev/null

note "downloading the latest release"
URL="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" |
  grep -o "https://[^\"]*${ASSET}" | head -1)"
[ -n "$URL" ] || die "the latest release carries no ${ASSET}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$URL" -o "$TMP/app.tar.gz"
mkdir -p "$INSTALL_DIR"
tar xzf "$TMP/app.tar.gz" -C "$INSTALL_DIR"
BINARY="$(find "$INSTALL_DIR" -maxdepth 2 -type f -name 'WebDataStudio.Server' | head -1)"
[ -n "$BINARY" ] || die "the archive holds no WebDataStudio.Server binary"
chmod +x "$BINARY"

id -u webdatastudio >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin webdatastudio
mkdir -p "$DATA_DIR"
chown -R webdatastudio:webdatastudio "$DATA_DIR" "$INSTALL_DIR"

# The password is generated once and kept here, so a second run does not lock out whoever wrote the
# first one down. With no user and no password the studio has no login screen at all, which is the one
# thing an installer must not leave behind.
if [ ! -f "$ENV_FILE" ]; then
  note "generating the login"
  cat > "$ENV_FILE" <<EOF
ASPNETCORE_URLS=http://0.0.0.0:${PORT}
DB_PATH=${DATA_DIR}/webdatastudio.db
WDS_USER=${ADMIN_USER}
WDS_PASSWORD=$(openssl rand -hex 18)
WDS_OPEN_BROWSER=false
WDS_BACKUP_DIR=${DATA_DIR}/backups
EOF
  chown root:webdatastudio "$ENV_FILE"
  chmod 640 "$ENV_FILE"
fi

# ASP.NET Core takes its content root from the working directory, and the studio's wwwroot sits next
# to the binary - point the unit anywhere else and every page is a 404 while the process looks healthy.
APP_DIR="$(dirname "$BINARY")"

note "writing the service"
cat > /etc/systemd/system/webdatastudio.service <<EOF
[Unit]
Description=WebDataStudio
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=webdatastudio
Group=webdatastudio
EnvironmentFile=${ENV_FILE}
WorkingDirectory=${APP_DIR}
ExecStart=${BINARY}
Restart=on-failure
RestartSec=5
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=${DATA_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable webdatastudio >/dev/null 2>&1 || true
systemctl restart webdatastudio

note "waiting for the studio to answer"
for _ in $(seq 1 40); do
  if curl -fsS -o /dev/null "http://127.0.0.1:${PORT}/"; then
    IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
    echo ""
    note "done"
    echo "    URL:      http://${IP:-127.0.0.1}:${PORT}"
    echo "    Login:    $(grep '^WDS_USER=' "$ENV_FILE" | cut -d= -f2-) / $(grep '^WDS_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)"
    echo "    Service:  systemctl status webdatastudio"
    echo "    Update:   run this script again"
    exit 0
  fi
  sleep 2
done

journalctl -u webdatastudio --no-pager -n 30 || true
die "the service did not answer on port ${PORT}"
