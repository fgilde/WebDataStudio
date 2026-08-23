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

## Eine Tabelle durchsehen

Eine mit Doppelklick geöffnete Tabelle hat dieselben Aktionen **Copy** und **Export** wie ein
Abfrage-Ergebnis: Kopieren nimmt die Seite auf dem Schirm, Export streamt die ganze Tabelle. Die
Spaltenköpfe haben ein Menü zum Sortieren und Filtern, und beides läuft auf dem Server — eine Seite
hält standardmäßig 200 von womöglich Millionen Zeilen, im Browser wäre also die falsche Menge
sortiert. Wie viele es sind, ist eine [Einstellung](shortcuts.md).

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
