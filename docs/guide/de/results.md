# Ergebnisse und Export

Ergebnisse kommen gestreamt an, noch während die Abfrage läuft — die ersten Zeilen stehen also lange
vor der letzten auf dem Schirm. Das Zeilenlimit (`WDS_MAX_ROWS`, Vorgabe 1000) lässt sich pro Lauf
anheben.

## Ansichten

Ein Schalter über dem Grid wählt, wie dasselbe Ergebnis gelesen wird:

| Ansicht | Gut für |
|---|---|
| Grid | die Vorgabe; virtualisiert, hunderttausend Zeilen scrollen flüssig |
| Form | eine Zeile nach der anderen, wenn eine Tabelle vierzig Spalten hat |
| Transposed | eine breite Tabelle mit wenigen Zeilen — Spalten werden Zeilen |
| Chart | Balken, Linie oder Kreis über eine Beschriftungs- und eine oder mehrere Zahlenspalten |
| Compare | zwei Ergebnisse nebeneinander, über Schlüsselspalten zugeordnet |

![Chart-Ansicht](../../assets/screenshots/chart-dark.png)

## Im Grid

- Sortieren, je Spalte filtern und im ganzen Ergebnis suchen.
- Spalten ausblenden, anheften, verschieben und in der Breite ziehen; Breiten werden je Spaltenname
  gemerkt.
- Nach einer Spalte gruppieren: jede Gruppe zeigt ihre Anzahl und die Summen der Zahlenspalten.
- Zellen markieren, und die Statusleiste zeigt Anzahl, Summe, Mittelwert, Minimum und Maximum.
- Doppelklick auf eine Zelle öffnet den Werteanzeiger: Text, JSON, XML, Hex, Bilder und
  BLOB-Download.
- `NULL` wird anders dargestellt als eine leere Zeichenkette, weil der Unterschied zählt.

## Export

Der Export-Dialog schreibt CSV, TSV, Excel, JSON, NDJSON, XML, YAML, Markdown, HTML, SQL-Inserts,
SQL-Schema und Parquet. Der Umfang ist das aktuelle Ergebnis, eine ganze Tabelle oder ein ganzes
Schema. Trennzeichen, Kodierung, Quoting, Kopfzeile, `NULL`-Darstellung und Datumsformat bestimmst
du.

Exporte streamen: der Server baut die Datei nie komplett im Speicher, eine CSV mit einer Million
Zeilen kostet also so viel Speicher wie eine mit tausend.

### Eigene Templates

**Templates…** im Export-Dialog schreibt ein eigenes Exportformat: eine Id, ein Name, eine
Dateiendung, ein Content-Type und bis zu drei Textstücke.

| Platzhalter | Ist |
|---|---|
| `{{table}}` | die Tabelle bzw. der Name des Exports |
| `{{columns}}` | die Spaltennamen, verbunden |
| `{{values}}` | die Werte der Zeile, verbunden |
| `{{col.name}}` | eine Spalte nach Namen |
| `{{index}}` | die Zeilennummer, ab eins |
| `{{comma}}` | ein Komma in jeder Zeile außer der letzten |

Jeder nimmt einen Filter für die Maskierung, die das Format braucht: `{{values|sql}}` sowie `json`,
`csv`, `html`, `upper`, `lower`. Ein `INSERT`-Schreiber sind damit drei Zeilen:

```
header: INSERT INTO {{table}} ({{columns}}) VALUES
row:      ({{values|sql}}){{comma}}
footer: ;
```

DataGrip nennt das Extractors und schreibt sie in Groovy — damit ist ein Exportformat ein Programm,
das das Studio ausführen müsste. Hier ist es Text, und darin gibt es nichts auszuführen. Ein hier
gespeichertes Template gehört diesem Studio; `WDS_EXPORT_TEMPLATES_DIR` bindet einen Ordner davon für
eine Bereitstellung ein, und die sind in der Oberfläche nur lesbar — eine Kopie unter anderer Id ist
der Weg, eines zu ändern.

## Kopieren

Das Menü **Copy** legt das Ergebnis als CSV, JSON oder Markdown-Tabelle in die Zwischenablage — und
eine Markierung als SQL-`IN`-Liste, der schnellste Weg, eine Menge Ids in die nächste Abfrage zu
bekommen.

## Import

**Import into this table…** im Kontextmenü des Explorers liest CSV, Excel, JSON oder SQL, zeigt eine
Vorschau und lässt dich Dateispalten auf Tabellenspalten abbilden. Fehlerhafte Zeilen werden
einzeln gemeldet, statt die ganze Datei abzubrechen.

**Copy to another connection…** verschiebt eine Tabelle zwischen zwei Verbindungen, auch über
Engine-Grenzen hinweg.

## Was in einer JSON-Spalte steht

Eine JSONB-Spalte ist im Grid eine Zelle Text, und eine Zeile davon zu lesen ist geraten. **What is
in this column?** im Menü einer JSON-Spalte liest eine Stichprobe der Dokumente und antwortet mit der
Struktur: welche Pfade es gibt, wie oft jeder vorkommt, welche Typen er hält, und ein Beispielwert.

Die Pfade sind nach Tiefe und dann nach erstem Auftreten geordnet — die Struktur liest sich also wie
das Dokument und nicht wie eine alphabetische Liste. Zu jedem Pfad gehört der Ausdruck, der ihn **auf
dieser Engine** liest (`->>`, `JSON_VALUE`, `json_extract_string`), und es gibt ein
**Flatten**-Statement, das die Wertpfade zu Spalten macht, fertig für einen Query-Tab. Arrays und
Objekte fehlen darin: eine Spalte kann keinen Teilbaum halten.

![Was in einer JSON-Spalte steht](../../assets/screenshots/json-shape-dark.png)

Die Stichprobe ist der ehrliche Teil daran. Der Bericht sagt, wie viele Dokumente er gelesen hat und
wie viele davon geparst haben; eine Spalte mit hundert Strukturen darin sagt das, statt die erste als
Wahrheit auszugeben.

## Eine Tabelle verfolgen

Eine Tabelle, in die geschrieben wird, lässt sich im Daten-Tab beobachten: Schlüsselspalte wählen —
eine Id, ein Zeitstempel, ein Auto-Increment —, dazu ein Intervall, und die Seite liest sich in dieser
Reihenfolge neu, das Neueste zuerst. **Zeilen, die seit dem letzten Lesen dazugekommen sind, werden
eingefärbt**, ein Insert ist also sichtbar, ohne zwei Screenshots zu vergleichen.

Angeboten werden nur Schlüsselspalten. Nach einem Fremdschlüssel zu sortieren und das Ergebnis
„neueste“ zu nennen wäre eine Lüge, die die Färbung glaubhaft macht — die Liste bleibt also bei dem,
was tatsächlich hochzählt.

## Aus einer Datei eine neue Tabelle

**New table from a file…** ist der andere Import: der für die CSV, die jemand geschickt hat, wo es noch
keine Tabelle gibt. Datei wählen — ein Upload oder ein Objekt in einem [Bucket](storage.md), das dort
gelesen wird, wo es liegt — und das Studio beschreibt sie zuerst: die gefundenen Spalten, welcher Typ
daraus auf der Ziel-Engine wird, zehn Zeilen so, wie sie ankommen werden, und das `CREATE TABLE`
selbst. Erzeugt wird nichts, bevor das gelesen ist. Verstanden werden Parquet, CSV, TSV, JSON und
NDJSON; ein ganzes Prefix aus Dateien derselben Form gilt als eine Tabelle.

## Eine Tabelle durchsehen

Eine mit Doppelklick geöffnete Tabelle hat dieselben Aktionen **Copy** und **Export** wie ein
Abfrage-Ergebnis: Kopieren nimmt die Seite auf dem Schirm, Export streamt die ganze Tabelle. Die
Spaltenköpfe haben ein Menü zum Sortieren und Filtern, und beides läuft auf dem Server — eine Seite
hält standardmäßig 200 von womöglich Millionen Zeilen, im Browser wäre also die falsche Menge
sortiert. Wie viele es sind, ist eine [Einstellung](shortcuts.md).

### Eine Engine ohne SQL

Für MongoDB und Redis baut der Daten-Tab kein `SELECT` — er fragt den Treiber nach der Seite, und der
baut sie so, wie seine Engine es kann:

- Eine **MongoDB-Collection** wird mit `find().sort().skip().limit()` gelesen. Der Spaltenfilter wird
  in die Abfrage übersetzt: `^ada` wird ein verankerter regulärer Ausdruck, `>10` ein `$gt` mit einer
  Zahl, `=a,=b` ein `$in`, `NULL` eine Null-Prüfung. Blättern und Sortieren passieren im Server, die
  Reihenfolge gilt also für die Collection und nicht für die Seite. Die Spalten sind die Form, die das
  Struktur-Panel gesampelt hat; ein Feld, das diese Seite zeigt und das im Sample nie vorkam, wird
  hinten angehängt und als `unsampled` markiert — Dokumente haben kein Schema, und genau deshalb
  zeigt man sie. Ein verschachteltes Dokument oder ein Array bleibt JSON in seiner Zelle, „was steht
  in diesem JSON“ aus dem Spaltenmenü gilt also auch dafür.
- Eine **Redis-Datenbank oder ein Präfix-Ordner** wird als die Schlüssel gelesen, die darin liegen:
  der Schlüssel, sein Typ, seine TTL in Sekunden, seine Länge und was er an Speicher kostet. Das ist
  die Inventarliste, die man von einem Cache tatsächlich will, und sie sortiert, filtert und
  exportiert wie jedes andere Gitter. Ein Schlüsselraum wird gescannt und nicht indiziert: eine Seite
  sieht sich die ersten 20 000 Schlüssel an, und die Fußzeile sagt es, wenn dort Schluss war.
  `MEMORY USAGE` gibt es nicht auf jedem gemanagten Redis; wo es abgelehnt wird, bleibt die Spalte
  leer, statt die Seite scheitern zu lassen.
- Ein **einzelner Redis-Schlüssel** wird als die Tabelle gelesen, die sein Typ ergibt: Feld und Wert
  bei einem Hash, Index und Wert bei einer Liste, die Mitglieder einer Menge, Mitglied und Score bei
  einem Sorted Set, Id und Felder bei einem Stream, der Wert und seine Länge bei einem String. Ein
  Doppelklick öffnet einen Schlüssel weiterhin im Schlüssel-Browser, dort lässt sich der Wert
  bearbeiten; das Gitter ist der andere Blick darauf — und der Weg, ihn zu sortieren oder zu filtern.

Zwei Dinge folgen daraus, dass es kein SQL gibt. Das Gitter ist **nur lesbar** und nennt stattdessen
den Befehl, der schreibt — `updateOne` für ein Dokument, `HSET` für ein Hash-Feld —, statt einen
Speichern-Knopf zu zeigen, der nicht funktionieren kann. Und das Spaltenmenü bietet keine Liste der
vorkommenden Werte an: die zu zählen ist ein `GROUP BY`, also wird das mit Begründung abgelehnt und
der Filter stattdessen getippt.

Was die Engine mit der Abfrage nicht machen konnte, steht in der Fußzeile neben der Zeilenzahl, statt
still verschluckt zu werden: ein Filter ohne Übersetzung, eine Sortierung, für die ein Schlüsselraum
keine eigene Ordnung hat, ein Scan, der an seiner Grenze aufhörte.

## Die Filtersprache

![Das Spaltenmenü: Filterfeld und die Werte der Spalte](../../assets/screenshots/filter-dark.png)

Jedes Spaltenfilter — im Abfrage-Ergebnis wie in der Tabellenansicht — liest eine kleine Sprache
statt einer Teilzeichenkette. Ein einfaches Wort heißt weiter „enthält", was es vorher auch hieß.

| Eingabe | Bedeutung |
|---|---|
| `ada` | enthält `ada` (Text) bzw. ist gleich (Zahl, Datum, Boolean) |
| `^ada`, `$son` | beginnt mit, endet mit |
| `+ada`, `~ada` | enthält, enthält nicht |
| `!^ada`, `!$son`, `!=ada`, `<>ada` | die Verneinungen |
| `=ada` | ist gleich |
| `>10`, `>=10`, `<10`, `<=10` | als Zahl verglichen, bei einer Datumsspalte als Datum |
| `NULL`, `NOT NULL` | kein Wert / ein Wert |
| `EMPTY`, `NOT EMPTY` | kein Wert oder leerer String / keins von beidem |
| `TODAY`, `YESTERDAY`, `TOMORROW` | dieser Tag |
| `THIS WEEK`, `LAST MONTH`, `NEXT YEAR`, … | der Zeitraum; Wochen beginnen am Montag |
| `2026`, `2026-08`, `2026-08-23` | Jahr, Monat, Tag — jeweils der ganze Zeitraum |
| `"zwei Wörter"` | ein Wert mit Leerzeichen, Komma oder führendem Operator |
| `>10 <20` | Leerzeichen ist UND |
| `=1,=2` | Komma ist ODER, und ODER bindet schwächer als UND |

Text wird auf jeder Engine ohne Rücksicht auf Groß- und Kleinschreibung verglichen: PostgreSQLs
`LIKE` unterscheidet sie, MySQLs nicht — dasselbe Filter fand also je nach Verbindung andere Zeilen.

Wie ein Ausdruck gelesen wird, entscheidet der deklarierte Spaltentyp. Ein Wert ist immer ein
Parameter; nichts Getipptes erreicht das SQL als Text. In der Tabellenansicht filtert der Server die
ganze Tabelle, im Abfrage-Ergebnis der Browser die zurückgekommenen Zeilen — beide lesen dieselbe
Sprache, und ein gemeinsames Fall-Korpus (`tests/filter-cases.json`) hält sie ehrlich.

Das Spaltenmenü der Tabellenansicht listet außerdem die **vorhandenen Werte mit ihrer Anzahl** als
Checkboxen, häufigste zuerst. Angehakte Werte landen als `=a,=b` im Filterfeld — eine Art zu tippen,
kein zweites Filter. Eine maskierte Spalte hat keine Liste: ihre Werte sind genau das Geheimnis.

## Zeitstempel und Zeitzonen

Ein Zeitstempel kommt vom Treiber als `2026-08-29T14:00:00.0000000Z` — korrekt, und nicht das, was
jemand liest. Das Gitter zeigt `2026-08-29 14:00:00`, behält Nachkommastellen nur, wenn sie etwas
sagen, und beim Überfahren steht weiterhin der Rohwert da — denn „welcher ist es denn nun wirklich“
ist genau die Frage, um die es geht.

**Welche Uhr.** *Show timestamps in* in den [Einstellungen](shortcuts.md) ist die Zone dieses
Rechners, UTC oder eine benannte Zone. Es ändert nur die Anzeige; auf dem Weg in die Datenbank wird
nichts umgeschrieben. Ist es nicht deine eigene Zone, sagt es die Fußzeile — `times in UTC` —, damit
ein Screenshot später nicht falsch gelesen wird.

**Ein Wert ohne Zone wird nie umgerechnet.** `timestamp without time zone` mit 14:00 bedeutet 14:00;
daraus 16:00 zu machen, weil der Leser in Berlin sitzt, wäre eine Erfindung. Solche Zellen sind
gepunktet unterstrichen und sagen beim Überfahren `no time zone`, und der Spaltenkopf sagt, welche
der beiden Sorten die Spalte ist (`timestamptz — stored with a time zone`). Aus genau diesem
Unterschied entsteht die Sorte Unfall, bei der der falsche Tag gelöscht wird.

## Pivot

Das Gitter beantwortet „was ist hier drin“, das Gruppieren „wie viele je Status“. **Pivot** ist die
dritte Frage — „wie viele je Status *und Monat*“ — und sie wird über die Zeilen beantwortet, die
schon auf dem Schirm sind, statt über ein `GROUP BY`, das man erst schreiben müsste.

Eine Spalte für die Zeilen wählen, eine für die Spalten, und was mit den Zahlen passieren soll:
Anzahl, Summe, Durchschnitt, kleinster, größter Wert. `Anzahl` braucht gar keine Wertspalte,
deshalb ist es die Voreinstellung. Ein Wert, der keine Zahl ist, wird weggelassen statt als Null
gezählt — ein Durchschnitt über „die mit einer Zahl“ ist eine Antwort, einer mit eingefalteten
Nulls nicht. Null bekommt einen eigenen Namen, `(none)`, denn danach zu gruppieren ist eine echte
Frage. Bei sechzig verschiedenen Spaltenwerten ist Schluss, und das steht da: ein Pivot mit
neunhundert Spalten ist ein Scrollbalken, keine Antwort.

## Ein Dashboard

**Tools → Dashboard** ist eine Seite mit Statements nebeneinander: die Zahl, nach der jeden Morgen
jemand fragt, die Tabelle, die nach einem Deployment geprüft wird, ein Balken pro Zeile für „wie
viele je Status“.

Eine Kachel besteht aus Titel, Verbindung, Statement und dem, was daraus gezeichnet wird — eine
Zahl, eine Tabelle oder ein Balken pro Zeile. Sie nimmt eine bis vier der vier Spalten ein, und die
Seite kann sich selbst in einem Intervall neu ausführen (frühestens alle zehn Sekunden; darunter ist
es ein Lasttest und kein Dashboard). Der Pfeil auf einer Kachel öffnet ihr Statement in einem
Abfrage-Tab — dort passiert alles, was über Hinsehen hinausgeht.

Nichts hier kann, was ein Abfrage-Tab nicht kann: eine Kachel läuft über denselben Endpunkt, mit
derselben Zeilengrenze, derselben Maskierung und derselben Zeile im
[Audit-Trail](administration.md). Eine Kachel, deren Statement scheitert, sagt das auf der Kachel,
statt ein leeres Kästchen zu zeigen — und es läuft immer nur eine Ausführung je Kachel, ein
Statement, das länger braucht als das Intervall, staut sich also nicht hinter sich selbst.

Dashboards liegen in der Workspace-Datei des Studios: sie überleben einen Neustart und gehören zum
Arbeitsbereich, nicht zu einem Browser. Ein Studio ohne Workspace sagt das, statt die Seite zu
verlieren.

## Karte

![Die Kartenansicht](../../assets/screenshots/map-dark.png)

Die Ansicht **Map** zeichnet, was an Geografie im Ergebnis steckt: eine Spalte mit GeoJSON (Text oder
Objekt), eine mit WKT (`POINT(13.4 52.5)`, `LINESTRING`, `POLYGON`, auch die `MULTI`-Formen, ein
`SRID=`-Präfix wird ignoriert) oder ein Spaltenpaar aus Breiten- und Längengrad.

Punkte, Linien und Flächen werden maßstäblich gezeichnet, mit den Grenzen der Daten in der Kopfzeile;
Hovern nennt die Zeile. Bewusst **ohne Basiskarte**: ein Container hat keinen Tile-Server, und ein
Datenbank-Studio, das von selbst einen im Internet anfragt, ist nichts, was man still ausliefert. Die
Ansicht beantwortet „liegen die Punkte, wo ich denke, und welcher ist der Ausreißer".

## Archive

![Das Archiv-Panel](../../assets/screenshots/archives-dark.png)

Ein Ergebnis lässt sich behalten: **Keep** neben Export schreibt es als Datei auf die Platte des
Studios, das Panel **Archives** listet, was da ist. Das beantwortet „wie sah das vor der Migration
aus" ohne eine zweite Datenbank.

- Die Zeilen werden erneut aus der Datenbank gelesen — ein Archiv ist das ganze Ergebnis, nicht die
  Seite auf dem Schirm.
- Format ist NDJSON: eine Kopfzeile mit Spalten, Zeitpunkt und Herkunft, danach eine Zeile je
  Datensatz als JSON-Array. Das liest jedes Werkzeug.
- Maskierte Spalten sind **in der Datei** maskiert. Ein Archiv davon wäre ein Weg um die Maskierung
  herum.
- Ein vorhandener Name wird ersetzt. **Keep as archive…** auf einer Tabelle im Explorer tut dasselbe
  für eine ganze Tabelle.

Ein geöffnetes Archiv zeigt seine Zeilen in einem normalen Grid. **Script the rows as INSERTs…**
schreibt sie für die Zieltabelle aus — als Skript, das im Editor landet und durch dieselbe Vorschau
geht wie jede andere Änderung. Archive liegen in `archives/` neben der Anwendungsdatenbank oder wo
`WDS_ARCHIVE_DIR` hinzeigt; `WDS_ARCHIVE_MAX_ROWS` (Standard 100 000) begrenzt, wie viele Zeilen
eines behält.

## Verlauf

`Strg+H` öffnet den Verlauf: jedes gelaufene Statement mit Zeitpunkt, Dauer, Zeilenzahl und Fehler.
Er liegt auf dem Server, ein Neustart des Containers verliert ihn also nicht. Ein Klick legt das
Statement zurück in einen Abfrage-Tab.

Ist in den Einstellungen **Keep the result with each history entry** an, behält ein erfolgreicher
Lauf auch, was er zurückgab. Solche Einträge tragen ein kleines Symbol; ein Klick darauf öffnet die
Zeilen von damals in einem normalen Grid, ohne noch einmal etwas auszuführen. Standardmäßig aus, und
das aus gutem Grund: ein Snapshot ist eine Kopie der Daten in der Workspace-Datenbank. Die Zeilenzahl
ist ebenfalls eine Einstellung, und ein Snapshot über einem Megabyte wird abgelehnt statt still
abgeschnitten.
