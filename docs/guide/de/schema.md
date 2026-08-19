# Schema bearbeiten

![Tabellen-Designer](../../assets/screenshots/designer-dark.png)

## Tabellen-Designer

**Design table…** im Kontextmenü des Explorers öffnet eine Tabelle: Spalten mit Typ, Nullbarkeit,
Vorgabewert, Identity und Kommentar; Indizes mit Eindeutigkeit, Filter und Include-Spalten, wo die
Engine sie kennt; Primärschlüssel, Fremdschlüssel, Unique- und Check-Constraints.

Jede Änderung ist zuerst eine Migrationsvorschau. Der Dialog zeigt die Statements, markiert die
destruktiven und sagt, ob sie in einer Transaktion laufen. Vor dem Anwenden erreicht nichts die
Datenbank.

SQLite kennt kein `ALTER COLUMN`; der Designer schreibt die Folge aus Anlegen, Kopieren, Löschen und
Umbenennen selbst und zeigt sie wie jede andere Änderung in der Vorschau.

## Indizes

**Indexes…** auf einer Tabelle öffnet den Designer auf seinem Index-Tab: Name, Spalten,
Eindeutigkeit, ein partielles Prädikat und Include-Spalten, wo die Engine sie kennt.
**Add index on this column…** auf einer Spalte startet denselben Editor mit dem passenden Index
schon vorbereitet.

Wo die Engine Volltext kennt — PostgreSQL und MySQL — macht der Schalter *Full text* daraus die
jeweilige Schreibweise: ein GIN-Index über `to_tsvector(...)` bei PostgreSQL, ein
`FULLTEXT INDEX` bei MySQL. Überall sonst bleibt der Schalter aus, statt ein Statement zu
schreiben, das scheitern würde.

Auch Index-Änderungen laufen durch dieselbe Vorschau wie jede andere Schema-Änderung.

## Skripte aus dem Kontextmenü

`Script: INSERT`, `UPDATE`, `DELETE`, `TRUNCATE` und `DROP` öffnen einen Abfrage-Tab mit dem fertig
geschriebenen Statement für das gewählte Objekt. Spalten, Indizes und Fremdschlüssel haben eigene:
`DROP COLUMN`, `DROP INDEX`, ein Rebuild, `DROP CONSTRAINT`. Destruktive Statements laufen nie aus einem Menü —
sie landen im Editor, wo du sie liest und selbst `F5` drückst.

## Umbenennen

**Rename…** zeigt das Statement zusammen mit dem, was vom Objekt abhängt. Eine Umbenennung, die
einen View zerlegen würde, ist damit eine Entscheidung und keine Überraschung.

## Views, Prozeduren, Funktionen, Trigger

Ihr Quelltext wird gezeigt und lässt sich bearbeiten, wo die Engine ihn preisgibt. Es gilt dieselbe
Regel: erst Vorschau, dann Anwenden.
