# Umgebungsvariablen

| Variable | Bedeutung |
|---|---|
| `WDS_CONNECTIONS` | JSON-Array mit Verbindungsobjekten, wird beim Start angewendet |
| `WDS_CONN_<NAME>` | eine Verbindung als URL oder als Provider-Verbindungszeichenfolge; der Name hinter dem Präfix wird ihr Label |
| `WDS_CONN_<NAME>_ENGINE` | zu welcher Engine diese Verbindungszeichenfolge gehört |
| `WDS_CONN_<NAME>_READONLY`, `_GROUP`, `_COLOR` | Flags für die gleichnamige Verbindung |
| `WDS_USER`, `WDS_PASSWORD` | sind **beide** gesetzt, schützt ein Login-Bildschirm die Anwendung |
| `WDS_TITLE` | ein Name für dieses Studio; steht in der Kopfleiste, auf dem Login-Bildschirm und im Browser-Tab |
| `WDS_SECRET_KEY` | AES-Schlüssel (Base64, 32 Byte) für gespeicherte Geheimnisse; sonst wird `/data/.key` erzeugt |
| `WDS_READONLY` | bei `true` ist jede Verbindung nur lesend, unabhängig von ihrem eigenen Flag |
| `WDS_QUERY_TIMEOUT_SECONDS` | Standard-Timeout je Statement, Vorgabe `300` |
| `WDS_MAX_ROWS` | Standard-Zeilenlimit je Ergebnis, Vorgabe `1000` |
| `WDS_STORAGE_PREVIEW_BYTES` | wie viel von einem Objekt die Vorschau liest, Vorgabe `65536` — siehe [Objektspeicher](storage.md) |
| `WDS_STORAGE_MAX_UPLOAD_BYTES` | größter Upload in einen Bucket, Vorgabe `67108864` |
| `WDS_DUCKDB_EXTENSION_DIR` | wo DuckDBs Speicher-Erweiterungen liegen, im Image `/opt/duckdb/extensions` |
| `WDS_CONN_<NAME>_SCHEMAS` | nur diese Schemas dieser Verbindung lesen — siehe [Objektspeicher](storage.md) und den Explorer |
| `WDS_EXPORT_TEMPLATES_DIR` | Ordner mit Export-Templates, die die Bereitstellung mitbringt — siehe [Ergebnisse und Export](results.md) |
| `WDS_MAX_SESSIONS` | offene Sitzungen je Verbindung, Vorgabe `8` |
| `WDS_IDLE_TIMEOUT_SECONDS` | wie lange eine ungenutzte Sitzung offen bleibt, Vorgabe `300` |
| `WDS_OPEN_BROWSER` | `true` öffnet beim Start einen Browser (Vorgabe der Desktop-Builds) |
| `WDS_ARCHIVE_DIR`, `WDS_ARCHIVE_MAX_ROWS` | wohin behaltene Ergebnisse geschrieben werden und wie viele Zeilen eines behält — siehe [Ergebnisse](results.md) |
| `WDS_APP_WINDOW` | `false` öffnet einen normalen Browser-Tab statt eines eigenen Fensters — siehe [Erste Schritte](getting-started.md) |
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

## Verbindungen als Provider-Verbindungszeichenfolge

Dieselbe Variable nimmt auch die Verbindungszeichenfolge, die ein Provider erzeugt — genau das, was
ein Orchestrator wie .NET Aspire ohnehin zur Hand hat. Die Engine dazu benennen:

```bash
WDS_CONN_SHOP="Host=db;Port=5432;Username=app;Password=pw;Database=shop"
WDS_CONN_SHOP_ENGINE=postgresql
WDS_CONN_SHOP_GROUP=Development
WDS_CONN_SHOP_READONLY=false
```

Ohne `_ENGINE` wird die Engine aus den Schlüsseln der Zeichenfolge erraten; schlägt das fehl, wird
die Verbindung übersprungen — besser, als sie am falschen Treiber anzuhängen.

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
