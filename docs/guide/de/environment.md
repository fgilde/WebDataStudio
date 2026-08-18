# Umgebungsvariablen

| Variable | Bedeutung |
|---|---|
| `WDS_CONNECTIONS` | JSON-Array mit Verbindungsobjekten, wird beim Start angewendet |
| `WDS_CONN_<NAME>` | eine Verbindung als URL; der Name hinter dem Präfix wird ihr Label |
| `WDS_USER`, `WDS_PASSWORD` | sind **beide** gesetzt, schützt ein Login-Bildschirm die Anwendung |
| `WDS_SECRET_KEY` | AES-Schlüssel (Base64, 32 Byte) für gespeicherte Geheimnisse; sonst wird `/data/.key` erzeugt |
| `WDS_READONLY` | bei `true` ist jede Verbindung nur lesend, unabhängig von ihrem eigenen Flag |
| `WDS_QUERY_TIMEOUT_SECONDS` | Standard-Timeout je Statement, Vorgabe `300` |
| `WDS_MAX_ROWS` | Standard-Zeilenlimit je Ergebnis, Vorgabe `1000` |
| `WDS_MAX_SESSIONS` | offene Sitzungen je Verbindung, Vorgabe `8` |
| `WDS_IDLE_TIMEOUT_SECONDS` | wie lange eine ungenutzte Sitzung offen bleibt, Vorgabe `300` |
| `WDS_OPEN_BROWSER` | `true` öffnet beim Start einen Browser (Vorgabe der Desktop-Builds) |
| `DB_PATH` | Anwendungsdatenbank (SQLite), Vorgabe `/data/webdatastudio.db` |
| `ASPNETCORE_URLS` | Listen-Adresse, Vorgabe `http://0.0.0.0:8080` |

## Verbindungen als URL

```bash
WDS_CONN_SHOP=postgres://app:pw@db:5432/shop
WDS_CONN_CACHE=redis://cache:6379
WDS_CONN_LOCAL=sqlite:///data/local.db
```

Erkannte Schemata: `postgres`, `postgresql`, `mysql`, `mariadb`, `sqlserver`, `mssql`, `sqlite`,
`oracle`, `duckdb`, `clickhouse`, `mongodb`, `redis`.

## Verbindungen als JSON

```json
[{
  "name": "prod-pg",
  "engine": "postgresql",
  "connectionString": "Host=db;Port=5432;Database=shop;Username=app;Password=secret",
  "readOnly": true,
  "color": "#e03131",
  "group": "Production"
}]
```

`readOnly` gilt im Treiber, nicht nur in der Oberfläche: ein Statement, das kein Lesen ist, wird
abgelehnt, bevor es die Datenbank erreicht. `color` färbt die Zeile der Verbindung im Explorer —
die billigste Art, einen Produktionsunfall zu verhindern.

## Geheimnisse

In der Oberfläche angelegte Verbindungen liegen in der Anwendungsdatenbank; die
Verbindungszeichenfolge — und ein SSH-Schlüssel, falls vorhanden — ist mit AES-GCM verschlüsselt.
Der Schlüssel kommt aus `WDS_SECRET_KEY` oder wird einmalig nach `/data/.key` geschrieben. Volume
und Schlüssel gehören zusammen: ohne Schlüssel sind die gespeicherten Verbindungen nicht mehr
lesbar.
