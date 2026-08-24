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

## Was das Struktur-Panel beantwortet

Neben Spalten, Indizes und Schlüsseln beantworten weitere Tabs die Fragen, die man sonst per Hand im
Katalog sucht: **Statistik** (Größe, Zeilen, tote Zeilen, letztes `VACUUM`/`ANALYZE`, jeder Index mit
Größe und Scan-Zahl), **Rechte** (wer was darf, samt `GRANT`/`REVOKE` als Statement),
**Abhängigkeiten** (was zerbricht, wenn sich das hier ändert) und **SQL** (das Objekt als
`CREATE`-Statement).

![Row Level Security und ihre Policies](../../assets/screenshots/policies-dark.png)

**Policies** — bei einer Tabelle: ob Row Level Security an ist, ob sie auch für den Eigentümer
erzwungen wird, und jede Policy mit ihrem Ausdruck. Das Feld darunter baut ein `CREATE POLICY`, der
Mülleimer ein `DROP POLICY`. Security an ohne Policy heißt „außer dem Eigentümer sieht niemand
etwas“, und der Tab sagt das auch. Nur PostgreSQL — es ist ein PostgreSQL-Feature, und die anderen
Engines sagen genau das statt nichts zu zeigen.

**Partitionen** — bei einer partitionierten Tabelle: die Strategie (`RANGE`, `LIST`, `HASH`) samt
Schlüssel, jede Partition mit Grenze, Größe und Zeilenschätzung. `DETACH` lässt die Daten als eigene
Tabelle zurück, `ATTACH` nimmt eine bestehende Tabelle auf und braucht die Grenze ausgeschrieben.
Beides sind Statements; *detach concurrently* blockiert keine Leser und läuft dafür nicht in einer
Transaktion.

![Eine Funktion und was ihr Lauf gemeldet hat](../../assets/screenshots/inspect-dark.png)

**Inspect** — bei einer Funktion oder Prozedur: Sprache, Rückgabetyp, deklarierte Parameter, der
Quelltext und ein **Lauf, der zurückgerollt wird**. Argumente eintragen, Knopf drücken: die Funktion
läuft in einer Transaktion, die immer zurückgerollt wird, und es erscheint, was zurückkam, wie lange
es dauerte und jedes `RAISE NOTICE` auf dem Weg.

Kein schrittweiser Debugger: keine Breakpoints, keine Variablen-Inspektion. Für PL/pgSQL deckt es
das ab, was dieses Debuggen praktisch ist. Zwei Dinge muss man wissen: Nebenwirkungen, die
PostgreSQL außerhalb der Transaktion führt — eine weitergezählte Sequenz, ein `dblink`-Aufruf —
überleben das Rollback, und eine schreibgeschützte Verbindung lehnt den Lauf ab, statt so zu tun,
als machte das Rollback ihn sicher.

## Eine Spalte von der anderen Seite des Schlüssels

![Eine geliehene Spalte](../../assets/screenshots/borrowed-dark.png)

Das Spaltenmenü der Tabellenansicht bietet die Spalten der Tabelle an, auf die ein Fremdschlüssel
zeigt: eine auswählen, und sie steht neben der Id, markiert als **borrowed**. Der Join passiert auf
dem Server, Sortieren und Filtern gelten weiter für die eigenen Spalten, und die geliehene Spalte ist
schreibgeschützt — eine Änderung dort wäre ein Update auf eine Zeile, die dieses Grid nicht adressiert.

Nur einspaltige Schlüssel: einem zusammengesetzten Schlüssel kann ein einzelner Wert nicht folgen.

## Perspective — eine Zeile und alles, was mit ihr zu tun hat

![Eine Perspective über verwandte Zeilen](../../assets/screenshots/perspective-dark.png)

Das Panel **Perspective** beginnt bei einer Tabelle und lässt eine Zeile aufklappen: worauf sie
zeigt, und was auf sie zeigt, jeweils verschachtelt, so tief wie man öffnet. Es liest denselben
Fremdschlüssel-Graphen, den das ER-Diagramm zeichnet — zu tippen ist nichts.

Jede Ebene ist eine Seite Zeilen, nicht die ganze Tabelle, und jede geöffnete Beziehung ist eine
Abfrage; deshalb sind sie zugeklappt, bis man sie will. Gefolgt wird nur einspaltigen Schlüsseln.

## Objekte des Servers im Baum

Unter einer PostgreSQL-Verbindung stehen neben den Schemas auch **Extensions** (mit Version),
**Rollen** (Superuser und Gruppen markiert), **Tablespaces**, **Publications** und
**Subscriptions** sowie je Schema **Typen und Domains**. Es sind reine Auflistungen: das Studio
benennt, was da ist, statt anzubieten, es zu ändern.

**Privileges on everything here…** auf einem Schema baut ein Skript statt eines Dialogs pro Tabelle:
bei PostgreSQL `GRANT … ON ALL TABLES IN SCHEMA`, dazu `ALTER DEFAULT PRIVILEGES`, wenn auch später
angelegte Tabellen gelten sollen — ohne diese zweite Hälfte ist die Tabelle von morgen nicht
abgedeckt. Ein materialisierter View hat **Script: REFRESH** und **Script: REFRESH CONCURRENTLY**;
concurrently hält ihn währenddessen lesbar und braucht einen Unique-Index.
