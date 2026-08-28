# Entwicklung

## Aufbau

```
src/WebDataStudio.Server   .NET-10-Minimal-API: Treiber, Endpunkte, Export, Analyse
web                        React 19 + Mantine + dockview + Monaco
tests                      xunit v3, echte Datenbanken über Testcontainers
docs                       diese Seite und die Funktionsmatrix
```

## Beide Hälften starten

```bash
# API auf :5000
ASPNETCORE_URLS=http://localhost:5000 DB_PATH=/tmp/wds.db \
  dotnet run --project src/WebDataStudio.Server

# SPA auf :5173, leitet /api an :5000 weiter
cd web && npm install && npm run dev
```

Der veröffentlichte Container liefert die gebaute SPA von derselben Herkunft aus — es gibt also
nirgends eine CORS-Konfiguration.

## Tests

```bash
dotnet test                      # Server; startet echte Datenbanken in Containern
cd web && npx vitest run         # SPA-Units
cd web && npm run smoke          # Browser-Prüfung gegen einen laufenden Server
cd web && npm run smoke:admin    # Diagramm-, Administrations- und Vergleichspanels
cd web && npm run smoke:p9       # Palette, gespeicherte Abfragen, Builder, Charts, Parameter
cd web && npm run smoke:storage  # ein Bucket: Baum, Objekt, Datei als Tabelle
cd web && npm run smoke:quality  # Datenqualität, Audit-Trail, Subset, JSON-Spalte
```

Die Server-Suite fährt eine Verhaltens-Suite gegen jedes Engine-Fixture — ein neuer Treiber erbt
also den ganzen Vertrag. Ein eigener Test prüft die Ehrlichkeit der Fähigkeiten: was ein Treiber als
nicht unterstützt meldet, muss eine Ausnahme werfen, statt still nichts zu tun.

## Eine Engine hinzufügen

1. `IDbDriver` umsetzen, meist abgeleitet von `AdoDriverBase`.
2. Eine `DriverCapabilities` deklarieren, die die Wahrheit sagt.
3. Ein Fixture zur Vertrags-Suite hinzufügen; dort zeigt sich die meiste Arbeit.
4. Die Engine in `ConnectionRegistry.KnownEngines` und in die URL-Schema-Zuordnung eintragen.

## Image bauen

```bash
docker build -t webdatastudio:dev .
docker run --rm -p 8080:8080 webdatastudio:dev
```

`scripts/verify-backup-roundtrip.sh` lässt dieses Image gegen ein echtes PostgreSQL laufen und
schickt einen Dump durch Backup- und Restore-Endpunkt.

## Dokumentation

Die Seite unter `docs/` ist reines HTML plus docsify, ohne Build-Schritt.
`web/scripts/screenshots.mjs` erzeugt die Screenshots in einem dunklen und einem hellen Theme gegen
einen laufenden Server neu.
