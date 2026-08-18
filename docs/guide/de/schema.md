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

## Skripte aus dem Kontextmenü

`Script: INSERT`, `UPDATE`, `DELETE`, `TRUNCATE` und `DROP` öffnen einen Abfrage-Tab mit dem fertig
geschriebenen Statement für das gewählte Objekt. Destruktive Statements laufen nie aus einem Menü —
sie landen im Editor, wo du sie liest und selbst `F5` drückst.

## Umbenennen

**Rename…** zeigt das Statement zusammen mit dem, was vom Objekt abhängt. Eine Umbenennung, die
einen View zerlegen würde, ist damit eine Entscheidung und keine Überraschung.

## Views, Prozeduren, Funktionen, Trigger

Ihr Quelltext wird gezeigt und lässt sich bearbeiten, wo die Engine ihn preisgibt. Es gilt dieselbe
Regel: erst Vorschau, dann Anwenden.
