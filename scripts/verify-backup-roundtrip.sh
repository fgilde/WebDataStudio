#!/usr/bin/env bash
# End-to-end check for backup and restore: builds nothing, runs the published image against a
# live PostgreSQL and round-trips a dump through it. Build the image first:
#   docker build -t webdatastudio:p8 .
set -euo pipefail

NET=wds-p8
PG=wds-p8-pg
APP=wds-p8-app
PASS='s3cret-p8'

cleanup() {
  docker rm -f "$APP" "$PG" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
[ "${KEEP:-0}" = "1" ] || trap cleanup EXIT
cleanup

docker network create "$NET" >/dev/null
docker run -d --name "$PG" --network "$NET" \
  -e POSTGRES_PASSWORD="$PASS" -e POSTGRES_DB=shop postgres:17-alpine >/dev/null

echo "waiting for postgres"
for _ in $(seq 1 60); do
  docker exec "$PG" pg_isready -U postgres >/dev/null 2>&1 && break
  sleep 1
done

docker exec -e PGPASSWORD="$PASS" "$PG" psql -U postgres -d shop -c \
  "CREATE TABLE people (id int primary key, name text not null);
   INSERT INTO people VALUES (1,'ada'),(2,'linus'),(3,'grace');" >/dev/null

docker run -d --name "$APP" --network "$NET" -p 8099:8080 \
  -e "WDS_CONN_PG=postgresql://postgres:$PASS@$PG:5432/shop" \
  webdatastudio:p8 >/dev/null

echo "waiting for the app"
for _ in $(seq 1 60); do
  curl -sf http://localhost:8099/api/connections >/dev/null 2>&1 && break
  sleep 1
done

ID=$(curl -s http://localhost:8099/api/connections | python -c "import sys,json;print(json.load(sys.stdin)[0]['id'])")
echo "connection $ID"

# --- backup ------------------------------------------------------------------
curl -s -X POST "http://localhost:8099/api/admin/backup/$ID" \
  -H 'content-type: application/json' -d '{}' -o /tmp/wds-backup.sql
grep -q "CREATE TABLE public.people" /tmp/wds-backup.sql || { echo "FAIL: no CREATE TABLE in the dump"; exit 1; }
grep -q "ada" /tmp/wds-backup.sql || { echo "FAIL: no data in the dump"; exit 1; }
echo "backup ok ($(wc -c < /tmp/wds-backup.sql) bytes)"

# The password must never reach a command line: pg_dump is invoked with PGPASSWORD only.
if grep -q -- "--password=" /tmp/wds-backup.sql; then echo "FAIL: password leaked"; exit 1; fi

# --- destroy and restore ------------------------------------------------------
docker exec -e PGPASSWORD="$PASS" "$PG" psql -U postgres -d shop -c "DROP TABLE people;" >/dev/null
LEFT=$(docker exec -e PGPASSWORD="$PASS" "$PG" psql -U postgres -d shop -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_name='people'")
[ "$LEFT" = "0" ] || { echo "FAIL: the table survived the drop"; exit 1; }

# A restore without the confirmation must be refused.
CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST "http://localhost:8099/api/admin/restore/$ID" \
  -F "file=@/tmp/wds-backup.sql" -F "confirm=wrong")
[ "$CODE" = "400" ] || { echo "FAIL: an unconfirmed restore returned $CODE"; exit 1; }
echo "unconfirmed restore refused"

curl -s -X POST "http://localhost:8099/api/admin/restore/$ID" \
  -F "file=@/tmp/wds-backup.sql" -F "confirm=shop" | tee /tmp/wds-restore.json
echo

ROWS=$(docker exec -e PGPASSWORD="$PASS" "$PG" psql -U postgres -d shop -tAc "SELECT count(*) FROM people")
[ "$ROWS" = "3" ] || { echo "FAIL: after the restore the table has $ROWS rows"; exit 1; }

# --- the tools really are in the image ----------------------------------------
for tool in pg_dump psql mysqldump mongodump redis-cli; do
  docker exec "$APP" which "$tool" >/dev/null || { echo "FAIL: $tool missing from the image"; exit 1; }
done

echo "P8 backup round-trip ok"
