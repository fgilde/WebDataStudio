# Dashboards

Ein Dashboard ist hier eine Leinwand: vierundzwanzig Spalten, Widgets, die sich ziehen und in der
Größe ändern lassen, eine Zeitspanne für die Seite und Variablen, die ihre Statements lesen.
**Tools → Dashboard.**

Nichts darauf ist ein zweiter Weg an die Daten. Das Statement eines Widgets läuft über denselben
Endpunkt wie ein Abfrage-Tab — oder über die Federation des Studios, wenn es mehrere Verbindungen
umfasst. Zeilenlimit, Maskierung, nur-lesende Verbindungen und die Audit-Zeile sind also dieselben
und nicht ein zweiter Satz mit eigenen Fehlern.

## Die Seite

Ansehen und Bearbeiten sind zwei Modi, Ansehen ist der Standard. Der Stift öffnet die Leinwand: ein
Widget wird an seiner Titelzeile gezogen, an der Ecke in der Größe geändert, und **Add widget**
zeigt die Palette. **Save** behält es, das Kreuz wirft den Entwurf weg.

- **Die Zeitspanne** steht in der Kopfzeile und gehört zur Seite. Im Ansichtsmodus ist sie deine
  eigene Wahl, solange du hinschaust; im Bearbeitungsmodus wird sie als Startwert mitgespeichert.
- **Refresh** sind Sekunden, und nichts unter zehn: ein Dashboard, das jede Sekunde neu läuft, ist
  ein Lasttest. Leer heißt: nur, wenn jemand fragt.
- **Das eigene Menü jedes Widgets** — die drei Punkte — lässt es neu laufen, öffnet sein Statement
  im Editor und verwandelt ein Diagramm in die Zeilen, aus denen es gemacht ist. Letzteres ist die
  Zugänglichkeits-Alternative und gleichzeitig der schnellste Weg zu sehen, warum ein Bild falsch
  aussieht.
- Ein Widget, das nicht zeichnen kann, sagt das **in seinem eigenen Rahmen**. Ein kaputtes Statement
  leert nie eine Seite.

## Die Widget-Typen

| Die Aufgabe | Typ |
|---|---|
| Eine Kennzahl | `Stat` — Schwellwerte färben sie, und der Zustand steht auch als Wort da |
| Eine Zahl gegen einen Bereich | `Gauge` — braucht Kleinstes und Größtes und zeichnet ohne beides nicht |
| Größe pro Kategorie | `Bar`, `StackedBar` — bei langen Beschriftungen von selbst waagerecht |
| Verlauf über Zeit | `Line`, `Area`, `StackedArea` |
| Eine Form, klein | `Sparkline` |
| Teil eines Ganzen, wenige Teile | `Pie` — höchstens acht Stücke, dann ein „Other" |
| Teil eines Ganzen, viele Teile | `Treemap` — ein Farbton, hell nach dunkel nach Größe |
| Dichte über zwei Dimensionen | `Heatmap` — eine sequentielle Rampe, nie kategorial |
| Wo die Zeilen sind | `GeoMap` — die eigene Karte des Studios: Formen maßstäblich, ohne Basiskarte, ohne Tile-Server |
| Fluss von einem zum anderen | `Sankey` |
| Zeilen als Zeilen | `Table`, `List` — `List` ist Beschriftung und Wert, für eine Top-Ten |
| Prosa | `Text` — Markdown, mit eingesetzten Variablen |
| Struktur | `Row` — ein Band, das eine Gruppe von Widgets benennt |

### Was die Einstellungen fragen

Die Schublade hält drei Fragen auseinander, weil es drei Fragen sind:

- **Source** — eine Verbindung und ein Statement, ein Ressourcenpfad (MongoDB, Redis, OData) oder
  mehrere Verbindungen gleichzeitig.
- **Columns** — welche Spalte die Dinge benennt, welche sie misst, und welche aus einer Wertspalte
  eine Serie pro Wert macht. Leer gelassen benennt die erste Spalte, die keine Zahl ist, die Zeilen,
  und jede numerische Spalte wird gezeichnet: `SELECT status, count(*)` braucht überhaupt keine
  Zuordnung.
- **Options** — Einheit, Dezimalstellen, Bereich, Legende, waagerecht, Schwellwerte.

Eine zweite Achse gibt es hier absichtlich nicht. Zwei Maße mit unterschiedlichen Größenordnungen
sind zwei Widgets, kleine Vielfache oder beide auf eine gemeinsame Basis indexiert — ein Diagramm
mit zwei Y-Achsen ist der eine Fehler, mit dem ein Dashboard lügt, und er ist hier nicht verfügbar.

## Ein Widget über mehrere Datenbanken

Als Quelle **Several connections** wählen. Jede bekommt ihr eigenes Statement und einen Alias; das
Statement des Widgets läuft dann über diese Aliase in DuckDB, genau wie im Federation-Panel — mit
einem Zeilenlimit pro Quelle und einer ehrlichen Auskunft darüber, wie viel kopiert wurde.

```sql
-- Quelle shop      → Alias shop_orders
SELECT to_char(placed_at, 'YYYY-MM-DD') AS day, count(*) AS orders FROM orders GROUP BY 1

-- Quelle warehouse → Alias handovers
SELECT CONVERT(char(10), handed_over, 23) AS day, count(*) AS handovers
FROM dbo.deliveries GROUP BY CONVERT(char(10), handed_over, 23)

-- das Widget
SELECT coalesce(o.day, d.day) AS day, o.orders, d.handovers
FROM shop_orders o FULL OUTER JOIN handovers d ON d.day = o.day
ORDER BY 1
```

PostgreSQL weiß, was bestellt wurde, und SQL Server weiß, was an einen Frachtführer übergeben wurde;
das Bild ist die Frage, die keine der beiden allein beantworten kann.

## Die Zeitspanne, im Statement

Die Spanne erreicht das SQL eines Widgets über Macros, die der Server für den Dialekt dieser Engine
auflöst:

| Macro | Wird zu |
|---|---|
| `$__timeFilter(placed_at)` | das `BETWEEN` dieser Engine über die Spanne, mit gebundenen Werten |
| `$__from`, `$__to` | Epoch-Millisekunden |
| `$__fromIso`, `$__toIso` | ISO-Zeitstempel |
| `$__interval`, `$__intervalMs` | eine Bucket-Breite aus der Spanne und der Breite des Widgets |

```sql
SELECT date_trunc('hour', placed_at) AS hour, count(*) AS orders
FROM orders
WHERE $__timeFilter(placed_at)
GROUP BY hour
ORDER BY hour
```

`now-24h`, `now-7d`, `now/d` („seit Mitternacht") und ein ISO-Zeitstempel funktionieren alle als
Spanne. Ein Macro, das dieses Studio nicht hat, bleibt genau stehen, und keines bedeutet in einem
Abfrage-Tab etwas — dort ist `$__timeFilter` Text, den jemand getippt hat.

## Variablen

**Variables** in der Bearbeitungs-Kopfzeile. Eine Variable nimmt ihre Werte aus einem Statement auf
einer Verbindung oder aus einer geschriebenen Liste, steht allein oder nimmt mehrere und kann ein
„All" anbieten. Sie erscheint als Bedienelement in der Kopfzeile und erreicht SQL auf drei Wege:

| Im Statement | Was ankommt |
|---|---|
| `$region`, `${region}` | der einzelne Wert, **als Parameter gebunden** |
| `${region:csv}` | die Liste, pro Dialekt gequotet: `'eu', 'us'` |

**Das ist eine Grenze, und es lohnt sich zu wissen, wo sie verläuft.** Ein Einzelwert wird gebunden,
steht also nie im Statement-Text und kann nichts anderes sein als ein Wert. Eine Liste muss inline —
kein Treiber bindet ein `IN` — also wird jeder Eintrag im Dialekt der Engine gequotet, und ein Wert
mit einem Zeilenumbruch wird abgelehnt, mit dem Namen der Variablen in der Meldung. Ein Dashboard
ist ein Dokument, das Leute sich zuschicken; es darf kein Weg sein, fremdes SQL auszuführen.

Ein `Text`-Widget setzt dieselben Variablen ein, damit eine Seite sagen kann, um welche Region es
geht.

## Grafana, rein und raus

Das Studio liest Grafana-Dashboards und schreibt sie.

- **Rein:** das Menü hinter `{ }` → **Paste JSON**. Unsere Form, die ältere Kachelform und ein
  Grafana-Dashboard werden alle erkannt, ohne dass jemand es sagt — auch ein Export aus deren API
  mit dem `{ "dashboard": … }` drumherum.
- **Raus:** **Export as Grafana JSON**, mit festgelegtem `schemaVersion`, bereit zum Einfügen dort.

| Grafana | Hier |
|---|---|
| `panels[].type` | `stat`, `gauge`, `timeseries`→`Line`, `barchart`→`Bar`, `piechart`→`Pie`, `table`, `text`, `geomap`, `heatmap`, `row` |
| `gridPos {x,y,w,h}` | die Position — beide haben vierundzwanzig Spalten, es ist also eine Kopie |
| `targets[].rawSql` | das Statement des Widgets; die Datasource-Uid wird über den Namen einer Verbindung zugeordnet |
| `fieldConfig.defaults` | Einheit, Dezimalstellen, Min, Max, Schwellwerte |
| `options.legend`, `stacking` | Legende und die gestapelte Form des Typs |
| `templating.list` | Variablen |
| `time.from`, `time.to` | die Zeitspanne |

Keine der beiden Richtungen behauptet, verlustfrei zu sein, und beide sagen, was sie getan haben.
**Was nicht mitkommt, ist ein Satz im Bericht und keine stillschweigend leere Kachel:** eine
Prometheus- oder Loki-`expr`, ein Paneltyp, den hier nichts zeichnet (er kommt als Tabelle seiner
Zeilen an), Transformationen, Alert-Regeln, Library-Panels, Annotationen. Das Panel, wie Grafana es
geschrieben hat, bleibt am Widget, damit ein Export die Felder zurückgibt, die dieses Studio nie
gelesen hat.

## Was eine Bereitstellung mitbringt

`WDS_DASHBOARD_FILE` nennt eine Datei, mehrere Dateien oder einen Ordner — und die Dateien dürfen in
jeder der drei Formen vorliegen, gemischt:

```bash
-e WDS_DASHBOARD_FILE=/data/dashboards
-v ./grafana-dashboards:/data/dashboards:ro
```

Ein Team mit dreizehn exportierten Grafana-Dashboards hat sie genau in dieser Form: die Variable auf
den Ordner zeigen lassen. Was eine Datei ist, wird pro Datei entschieden, und was nicht mitkommen
konnte, steht im Log des Studios.

Ein Dashboard, das zur Bereitstellung gehört, gehört ihr auch: das Studio zeigt es mit einer
Kennzeichnung und kann es nicht ändern oder löschen. **Duplicate** macht eine Kopie, die dir gehört.

Aus einem App-Host sind dieselben zwei Dinge `WithDashboards` mit dem Widget-Modell und
`WithGrafanaDashboards("./dashboards")` — siehe die README des Aspire-Pakets.

## Farbe über dreiundzwanzig Themes

Diagramme bekommen nicht eine Palette pro Theme. Sie bekommen eine validierte kategoriale
Reihenfolge in zwei Stufensets — eines für helle Flächen, eines für dunkle — damit dieselbe Serie
überall dieselbe Farbe behält, und beide Sets bestehen die Prüfungen auf Farbfehlsichtigkeit und
Kontrast, gegen die sie geprüft wurden.

Was daraus folgt und pro Widget nicht verstellbar ist:

- **Farbe wird in fester Reihenfolge zugewiesen und nie durchgereicht.** Ab der neunten Serie faltet
  alles in „Other"; ein erzeugter Farbton wäre eine Farbe, die niemand validiert hat.
- **Ab zwei Serien gibt es eine Legende**, und bis vier stehen zusätzlich Direktbeschriftungen —
  Identität hängt nie allein an der Farbe.
- **Ein Maß ist ein Farbton, hell nach dunkel.** Eine Heatmap und eine Treemap sind keine Mengen von
  Kategorien.
- **Die vier Schwellwert-Zustände sind reserviert** — good, warning, serious, critical — und werden
  nie als „Serie vier" wiederverwendet. Ein Zustand wird mit seiner Zahl gezeigt, nicht nur als
  Farbe.
- Werte, Beschriftungen und Legenden tragen die Schriftfarbe des Themes; die farbige Markierung
  daneben trägt die Identität.

## Nicht dabei

Alarme und Benachrichtigungsregeln. Annotationen. Geplanter PDF- oder Bild-Export — für eine Datei
nach Zeitplan hat das Studio geplante Abfragen. Live-streamende Panels. Eine Datasource, die keine
Verbindung dieses Studios ist: kein Prometheus, kein Loki. Eine Plugin-Schnittstelle für fremde
Widget-Typen.
