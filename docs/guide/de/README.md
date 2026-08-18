# WebDataStudio

Ein Datenbank-Studio, das im Browser läuft. Ein Container bringt das ganze Studio mit:
Verbindungen, Abfrage-Editor, bearbeitbare Ergebnisse, Schema-Werkzeuge, Ausführungspläne,
Vergleich und Administration.

```bash
docker run -d -p 8080:8080 -v wds-data:/data \
  -e WDS_CONN_LOCAL="postgres://app:pw@db:5432/shop" \
  ghcr.io/fgilde/webdatastudio
```

<http://localhost:8080> öffnen. Ohne `WDS_USER` und `WDS_PASSWORD` gibt es keinen Login-Bildschirm.

![Abfrage-Editor und Ergebnis-Grid](../../assets/screenshots/query-dark.png)

## Wo anfangen

- [Erste Schritte](getting-started.md) — starten, eine Datenbank anhängen, die erste Abfrage.
- [Umgebungsvariablen](environment.md) — alles, was der Container beim Start liest.
- [Engine-Funktionsumfang](engines.md) — was jede der neun Engines unterstützt.

## Was es nicht ist

WebDataStudio ersetzt weder dein Migrationswerkzeug noch dein Monitoring noch deinen Backup-Plan.
Es ist der Ort, an dem ein Mensch auf eine Datenbank schaut und absichtlich etwas ändert — mit dem
Statement vor Augen, bevor es läuft.
