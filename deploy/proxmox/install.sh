#!/usr/bin/env bash
# WebDataStudio on Proxmox VE: an unprivileged Debian container with Docker and the studio in it.
#
# Run it on the PVE host as root:
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/fgilde/WebDataStudio/master/deploy/proxmox/install.sh)"
#
# Self-contained on purpose. The community helper scripts source a shared build.func from another
# repository at run time, which is convenient right up to the day that file moves. This one needs
# pct, which every PVE host has.
#
# Overridable: CTID, HOSTNAME_, DISK_GB, RAM_MB, CORES, BRIDGE, STORAGE, TEMPLATE_STORAGE, PORT, WDS_USER
set -euo pipefail

CTID="${CTID:-}"
HOSTNAME_="${HOSTNAME_:-webdatastudio}"
DISK_GB="${DISK_GB:-8}"
RAM_MB="${RAM_MB:-2048}"
CORES="${CORES:-2}"
BRIDGE="${BRIDGE:-vmbr0}"
STORAGE="${STORAGE:-local-lvm}"
TEMPLATE_STORAGE="${TEMPLATE_STORAGE:-local}"
PORT="${PORT:-8095}"
WDS_USER="${WDS_USER:-admin}"

die() { echo "webdatastudio: $*" >&2; exit 1; }
note() { echo "==> $*"; }

command -v pct >/dev/null || die "this runs on a Proxmox VE host: pct was not found"
[ "$(id -u)" -eq 0 ] || die "run as root"

[ -n "$CTID" ] || { CTID="$(pvesh get /cluster/nextid)"; note "no CTID given, taking the next free one: $CTID"; }

TEMPLATE="$(pveam available --section system 2>/dev/null | awk '/debian-13-standard/{print $2}' | sort | tail -1)"
[ -n "$TEMPLATE" ] || TEMPLATE="$(pveam available --section system 2>/dev/null | awk '/debian-12-standard/{print $2}' | sort | tail -1)"
[ -n "$TEMPLATE" ] || die "no Debian template offered by pveam"
if ! pveam list "$TEMPLATE_STORAGE" 2>/dev/null | grep -q "$TEMPLATE"; then
  note "downloading the template $TEMPLATE"
  pveam update >/dev/null 2>&1 || true
  pveam download "$TEMPLATE_STORAGE" "$TEMPLATE"
fi

note "creating the container $CTID"
# nesting is what lets Docker run inside an unprivileged container; keyctl is what containerd wants.
pct create "$CTID" "${TEMPLATE_STORAGE}:vztmpl/${TEMPLATE}" \
  --hostname "$HOSTNAME_" \
  --cores "$CORES" --memory "$RAM_MB" --swap 512 \
  --rootfs "${STORAGE}:${DISK_GB}" \
  --net0 "name=eth0,bridge=${BRIDGE},ip=dhcp" \
  --features nesting=1,keyctl=1 \
  --unprivileged 1 --onboot 1 >/dev/null
pct start "$CTID"

note "waiting for the network"
IP=""
for _ in $(seq 1 30); do
  IP="$(pct exec "$CTID" -- bash -c "hostname -I 2>/dev/null | awk '{print \$1}'" 2>/dev/null || true)"
  [ -n "$IP" ] && break
  sleep 2
done
[ -n "$IP" ] || die "the container did not get an address"

note "installing Docker"
pct exec "$CTID" -- bash -lc '
  set -e
  export DEBIAN_FRONTEND=noninteractive
  . /etc/os-release
  apt-get update -qq
  apt-get install -y -qq ca-certificates curl openssl >/dev/null
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin >/dev/null
  systemctl enable --now docker >/dev/null 2>&1 || true
'

note "writing the compose file"
TMP="$(mktemp)"
cat > "$TMP" <<'YAML'
services:
  webdatastudio:
    image: ghcr.io/fgilde/webdatastudio:latest
    restart: unless-stopped
    ports:
      - "${PORT}:8080"
    environment:
      WDS_USER: "${WDS_USER}"
      WDS_PASSWORD: "${WDS_PASSWORD}"
    volumes:
      - wds-data:/data
volumes:
  wds-data:
YAML
pct exec "$CTID" -- mkdir -p /opt/webdatastudio
pct push "$CTID" "$TMP" /opt/webdatastudio/docker-compose.yml
rm -f "$TMP"

# The password is generated inside the container and kept in its .env, so running this script again
# does not lock out anyone who wrote the first one down. Without a password the studio has no login
# screen at all, which is why it is generated rather than left to a later decision.
note "starting WebDataStudio"
pct exec "$CTID" -- bash -lc '
  set -e
  cd /opt/webdatastudio
  if [ ! -f .env ]; then
    {
      echo "PORT='"$PORT"'"
      echo "WDS_USER='"$WDS_USER"'"
      echo "WDS_PASSWORD=$(openssl rand -hex 18)"
    } > .env
    chmod 600 .env
  fi
  docker compose pull -q
  docker compose up -d
'

PASSWORD="$(pct exec "$CTID" -- bash -lc "grep '^WDS_PASSWORD=' /opt/webdatastudio/.env | cut -d= -f2-")"

echo ""
note "done"
echo "    URL:      http://${IP}:${PORT}"
echo "    Login:    ${WDS_USER} / ${PASSWORD}"
echo "    Update:   pct exec ${CTID} -- bash -lc 'cd /opt/webdatastudio && docker compose pull && docker compose up -d'"
echo "    Password: pct exec ${CTID} -- bash -lc 'cat /opt/webdatastudio/.env'"
