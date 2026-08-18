# Vergleich

![Vergleichs-Panel](../../assets/screenshots/compare-light.png)

## Schemas

Zwei Verbindungen wählen, und der Vergleich listet Tabellen nur in der Quelle, Tabellen nur im Ziel,
Tabellen mit Unterschieden Spalte für Spalte und übereinstimmende Tabellen. Das Sync-Skript, das
das Ziel wie die Quelle aussehen ließe, entsteht im Dialekt des Ziels und steht in einem
Diff-Editor.

Aus dem Vergleichs-Panel läuft dieses Skript nie. Kopiere es in einen Abfrage-Tab und lies es
zuerst — ein Sync-Skript enthält `DROP`-Statements.

## Daten

Zwei Tabellen und die Schlüsselspalten wählen, über die Zeilen zugeordnet werden; ohne Auswahl wird
der Primärschlüssel genommen. Der Vergleich läuft beide Seiten in Schlüsselreihenfolge ab — der
Speicherbedarf bleibt also flach, egal wie groß die Tabellen sind — und meldet im Ziel fehlende
Zeilen, nur dort vorhandene Zeilen und abweichende Zeilen mit Namen der geänderten Spalten.

Das erzeugte Skript ist `INSERT` für Fehlendes, `UPDATE` für Abweichendes und `DELETE` für
Überzähliges.

## Zwei Ergebnisse

Im Abfrage-Tab vergleicht die Ansicht **Compare** zwei Ergebnisse, die schon auf dem Schirm sind —
ohne zweiten Roundtrip und ohne das Risiko, zwei verschiedene Zeitpunkte zu vergleichen.
