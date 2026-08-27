# Engine-Funktionsumfang

Jeder Treiber gibt an, was seine Engine unterstützt — die Oberfläche blendet den Rest aus. Was ein
Treiber angibt, muss er auch können: ein Test prüft, dass alles als nicht unterstützt Gemeldete
eine Ausnahme wirft, statt still nichts zu tun.

| Capability | PostgreSQL | MySQL | SQL Server | SQLite | Oracle | DuckDB | ClickHouse | MongoDB | Redis | Storage |
|---|---|---|---|---|---|---|---|---|---|---|
| SQL | ja | ja | ja | ja | ja | ja | ja | — | — | ja |
| Schemas | ja | — | ja | — | ja | ja | ja | — | — | — |
| Several databases | ja | ja | ja | — | — | — | ja | ja | ja | — |
| Transactions | ja | ja | ja | ja | ja | ja | — | — | — | — |
| DDL | ja | ja | ja | ja | ja | ja | ja | — | — | — |
| Views | ja | ja | ja | ja | ja | ja | ja | — | — | — |
| Materialised views | ja | — | — | — | ja | — | ja | — | — | — |
| Stored procedures | ja | ja | ja | — | ja | — | — | — | — | — |
| Triggers | ja | ja | ja | ja | ja | — | — | — | — | — |
| Sequences | ja | — | ja | — | ja | ja | — | — | — | — |
| Foreign keys | ja | ja | ja | ja | ja | ja | — | — | — | — |
| Partial indexes | ja | — | ja | ja | — | — | — | — | — | — |
| Include columns | ja | — | ja | — | — | — | — | — | — | — |
| Estimated plan | ja | ja | ja | ja | ja | ja | ja | ja | — | — |
| Actual plan | ja | ja | ja | — | — | ja | — | ja | — | — |
| Backup | ja | ja | ja | ja | — | — | — | ja | ja | — |
| Restore | ja | ja | — | — | — | — | — | ja | — | — |
| User management | ja | ja | ja | — | ja | — | — | — | — | — |
| Session list | ja | ja | ja | — | ja | — | ja | ja | ja | — |
| Kill session | ja | ja | ja | — | ja | — | ja | ja | ja | — |
| Server metrics | ja | ja | ja | — | ja | — | ja | ja | ja | — |
| Slow queries | ja | ja | ja | — | — | — | — | — | — | — |
| Geplante Jobs | ja | ja | ja | — | — | — | — | — | — | — |
| Maintenance commands | ja | ja | ja | ja | ja | ja | ja | ja | ja | — |

MongoDB und Redis sind keine SQL-Engines: ihre Abfrage-Tabs nehmen die Befehle der jeweiligen
Engine, und Dokument-Ergebnisse erscheinen als JSON-Baum mit Tabellenansicht für flache Dokumente.

DuckDB und SQLite sind Dateien — also keine Sitzungen, keine Benutzer, keine zweite Datenbank zum
Umschalten. SQLite sichert sich selbst mit `VACUUM INTO`; ein Restore hieße, die Datei unter einer
offenen Verbindung zu ersetzen, und das macht die Anwendung bewusst nicht.

Storage ist ein Bucket und keine Datenbank — ein S3-kompatibler Endpunkt, Azure Blob Storage, Google
Cloud Storage oder ein Ordner. Es hat keine Schemas, keine Schlüssel und nichts, wogegen sich DDL
schreiben ließe: eine Datei wird gelesen und abgefragt, aber nie zeilenweise bearbeitet. Gelesen wird
über DuckDB, von dort kommt auch der Plan. Eine genannte Grenze: DuckDB erreicht Google Cloud Storage
über das S3-Protokoll, das HMAC-Schlüssel will — mit einem Service-Account allein funktionieren Baum,
Vorschau und Download, eine Abfrage nicht. Siehe [Objektspeicher](storage.md).
