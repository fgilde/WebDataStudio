#!/usr/bin/env bash
# WebDataStudio on Proxmox VE: an unprivileged Debian container with the studio in it.
#
# Run it on the PVE host as root:
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/fgilde/WebDataStudio/master/deploy/proxmox/webdatastudio.sh)"
#
# No Docker in the container. The release is a single self-contained binary, and Docker inside an
# unprivileged LXC starts nothing on a current Proxmox anyway: runc writes
# net.ipv4.ip_unprivileged_port_start, and /proc/sys is read-only in there. A privileged container
# would fix that by handing the container root on the host, which is a poor trade for one process.
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
INSTALLER="${INSTALLER:-https://raw.githubusercontent.com/fgilde/WebDataStudio/master/deploy/proxmox/install.sh}"

die() { echo "webdatastudio: $*" >&2; exit 1; }
note() { echo "==> $*"; }

command -v pct >/dev/null || die "this runs on a Proxmox VE host: pct was not found"
[ "$(id -u)" -eq 0 ] || die "run as root"

[ -n "$CTID" ] || { CTID="$(pvesh get /cluster/nextid)"; note "no CTID given, taking the next free one: $CTID"; }

pveam update >/dev/null 2>&1 || true

pick_template() {
  pveam available --section system 2>/dev/null | awk -v pat="$1" '$2 ~ pat {print $2}' | sort -V | tail -1
}

# Newest first, but an older PVE refuses a newer Debian outright ("unsupported debian version") and
# only says so at create time - so the fallback is a second create, not a cleverer check.
CREATED=0
for pattern in debian-13-standard debian-12-standard; do
  TEMPLATE="$(pick_template "$pattern")"
  [ -n "$TEMPLATE" ] || continue
  if ! pveam list "$TEMPLATE_STORAGE" 2>/dev/null | grep -q "$TEMPLATE"; then
    note "downloading the template $TEMPLATE"
    pveam download "$TEMPLATE_STORAGE" "$TEMPLATE" >/dev/null
  fi
  note "creating the container $CTID from $TEMPLATE"
  if pct create "$CTID" "${TEMPLATE_STORAGE}:vztmpl/${TEMPLATE}" \
      --hostname "$HOSTNAME_" \
      --cores "$CORES" --memory "$RAM_MB" --swap 512 \
      --rootfs "${STORAGE}:${DISK_GB}" \
      --net0 "name=eth0,bridge=${BRIDGE},ip=dhcp" \
      --unprivileged 1 --onboot 1 >/dev/null 2>&1; then
    CREATED=1
    break
  fi
  note "this PVE will not create a container from $TEMPLATE, trying an older Debian"
done
[ "$CREATED" = "1" ] || die "no Debian template this PVE accepts"
pct start "$CTID"

note "waiting for the network"
IP=""
for _ in $(seq 1 30); do
  IP="$(pct exec "$CTID" -- bash -c "hostname -I 2>/dev/null | awk '{print \$1}'" 2>/dev/null || true)"
  [ -n "$IP" ] && break
  sleep 2
done
[ -n "$IP" ] || die "the container did not get an address"

note "installing WebDataStudio"
pct exec "$CTID" -- bash -lc "apt-get update -qq && apt-get install -y -qq --no-install-recommends curl ca-certificates >/dev/null"
pct exec "$CTID" -- bash -lc "WDS_PORT=${PORT} WDS_USER=${WDS_USER} bash -c \"\$(curl -fsSL ${INSTALLER})\""

PASSWORD="$(pct exec "$CTID" -- bash -lc "grep '^WDS_PASSWORD=' /etc/webdatastudio.env | cut -d= -f2-")"

echo ""
note "done"
echo "    URL:      http://${IP}:${PORT}"
echo "    Login:    ${WDS_USER} / ${PASSWORD}"
echo "    Update:   pct exec ${CTID} -- bash -c \"\$(curl -fsSL ${INSTALLER})\""
echo "    Password: pct exec ${CTID} -- cat /etc/webdatastudio.env"
