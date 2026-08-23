# Administration

![Administration](../../assets/screenshots/admin-dark.png)

## Wartung

Ein Katalog von Befehlen je Engine: `VACUUM`, `ANALYZE`, `REINDEX` bei PostgreSQL, `OPTIMIZE`,
`CHECK`, `FLUSH` bei MySQL, `DBCC CHECKDB` und Index-Rebuilds beim SQL Server und so weiter. Die
destruktiven sind markiert und fragen vor dem Ausführen nach.

Der Endpunkt nimmt eine Befehls-Id aus diesem Katalog, nie rohes SQL, und quotet das Ziel über den
Dialekt — dieses Panel kann also keine zweite, ungeloggte Abfragekonsole werden.

## Sitzungen

Die Sitzungsliste zeigt, wer verbunden ist, was läuft, wie lange es schon dauert und wer wen
blockiert. Eine Sitzung lässt sich beenden, nach einer Rückfrage, die ihr aktuelles Statement zeigt.

## Datenbanken

Datenbanken auflisten, anlegen und löschen — bei den Engines, die mehr als eine haben. Das Löschen
verlangt, dass du den Namen tippst.

## Benutzer und Rechte

Benutzer auflisten sowie anlegen oder ein Recht vergeben, über dieselbe Vorschau-dann-Anwenden-
Abfolge wie im Rest der Anwendung: erst steht das Statement da, dann läuft es.

## Server-Log

Wird gezeigt, wo die Engine es über SQL preisgibt. Wo nicht, sagt das Panel welche Engine und warum,
statt ein leeres Feld zu zeigen.

## Backup und Restore

Backups nutzen das Werkzeug der Engine selbst — `pg_dump`, `mysqldump`, `mongodump`,
`redis-cli --rdb` — und streamen das Ergebnis direkt in deinen Browser. SQLite kopiert sich per
`VACUUM INTO`; der SQL Server schreibt eine `.bak` auf dem Datenbankserver und meldet den Pfad.

Passwörter erreichen diese Werkzeuge über die Umgebung, nie als Kommandozeilenargument, das jeder
Prozess auf der Maschine lesen könnte.

Der Restore lädt einen Dump hoch und verlangt zuerst den Namen der Zieldatenbank. Es ist die eine
Aktion in der Anwendung, die eine ganze Datenbank überschreibt.

### Optionen

Bei PostgreSQL bietet das Panel, was `pg_dump` bietet: Format (`plain`, `custom`, `tar`),
Kompression von 0 bis 9, *No owner* und *Include DROPs (clean)*. Die Datei heißt nach dem, was sie
ist — ein Custom-Dump heißt nie `.sql`, denn den kann niemand zweimal einspielen. *Clean* gehört zum
Plain-Dump; die anderen Formate entscheiden das beim Restore, und die Anfrage wird dort abgelehnt
statt still verworfen. Die übrigen Engines kennen davon nichts, ihr Dialog zeigt es deshalb nicht —
und erreicht eine solche Option den Server trotzdem, wird sie abgelehnt statt ignoriert.

### Fortschritt

Ein Dump hat vorab keine Länge, das Werkzeug läuft noch, während die Bytes ankommen. Das Panel zählt
mit und zeigt es neben dem Knopf. Scheitert das Werkzeug auf halbem Weg, sind die gesendeten Bytes
nicht mehr zurückzuholen — ein Plain-Dump endet dann mit einem Kommentar, welches Werkzeug nach wie
vielen Bytes und warum aufgegeben hat.

## Überblick

Der erste Tab beantwortet, was die anderen Tabs nur gemeinsam beantworten könnten: Verbindungen,
Cache-Trefferquote, wartende Sitzungen, laufende Statements, das längste davon und die Größe der
Datenbank. Jede Kachel behält die letzten Messwerte, damit eine steigende Zahl anders aussieht als
eine bloß hohe.

Darunter dieselben Zahlen als Linien über fünf, fünfzehn oder dreißig Minuten — Sitzungen und
Durchsatz. Jede Linie ist auf ihren eigenen Bereich normiert: es geht um den Verlauf, und
Verbindungen und eine Trefferquote haben keine gemeinsame Einheit. Gemessen wird alle fünf Sekunden,
dieselbe Abfrage wie für die Kacheln; eine halbe Stunde wird behalten, auf dem Server nichts
gespeichert.
