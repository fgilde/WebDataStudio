# Administration

![Administration](../../assets/screenshots/admin-dark.png)

## Wartung

Ein Katalog von Befehlen je Engine: `VACUUM`, `ANALYZE`, `REINDEX` bei PostgreSQL, `OPTIMIZE`,
`CHECK`, `FLUSH` bei MySQL, `DBCC CHECKDB` und Index-Rebuilds beim SQL Server und so weiter. Die
destruktiven sind markiert und fragen vor dem Ausführen nach.

Der Endpunkt nimmt eine Befehls-Id aus diesem Katalog, nie rohes SQL, und quotet das Ziel über den
Dialekt — dieses Panel kann also keine zweite, ungeloggte Abfragekonsole werden.

## Jobs

Was der Server selbst nach Plan ausführt, egal wie es dort heißt: SQL-Server-Agent-Jobs, pg_cron-
Einträge, MySQL-Events. Ein Tab, denn die Frage ist dieselbe — was läuft, wann, und hat es
funktioniert. Jede Zeile trägt den Plan, das Ergebnis des letzten Laufs und den nächsten Termin; ein
Klick auf einen Job öffnet seine Historie.

Lesen ist frei. Ändern nicht: **Enable**, **Disable** und **Run now** erzeugen ein Statement in einem
Abfragetab, das dann denselben Weg geht wie alles Handgetippte. pg_cron und MySQL haben kein „jetzt
ausführen“ und sagen das, statt heimlich einen Job-Körper auszuführen.

Eine leere Liste ist kein Fehler — pg_cron kann fehlen, der Agent-Dienst aus sein, der Event-Scheduler
abgeschaltet — und der Tab sagt, in welchem Scheduler er nachgesehen hat. Eine Engine ohne eigenen
Scheduler sagt stattdessen genau das.

## Aufzeichnen

„Was läuft in der nächsten Minute auf diesem Server?“ Zeitfenster wählen, **Capture** drücken, und das
Studio liest einmal pro Sekunde die eigene Liste des Servers und gruppiert, was es sieht, nach
Statement — das längste zuerst, mit Anzahl der Sichtungen, Benutzer und ob es blockiert war.

Das ist Sampling, kein Tracing: ein Statement, das zwischen zwei Messungen beginnt und endet, wird
nicht gesehen, und der Tab sagt das. Extended Events und Äquivalente sind die richtige Antwort auf
diese Frage und brauchen Rechte, die ein Studio nicht einfordern sollte. Eine Aufzeichnung lässt sich
früher stoppen und behält, was sie gesehen hat; eine, die schon lief, wird beim Öffnen aufgenommen.

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

![Das Dashboard über die Zeit](../../assets/screenshots/dashboard-dark.png)

Darunter dieselben Zahlen als Linien über fünf, fünfzehn oder dreißig Minuten — Sitzungen und
Durchsatz. Jede Linie ist auf ihren eigenen Bereich normiert: es geht um den Verlauf, und
Verbindungen und eine Trefferquote haben keine gemeinsame Einheit. Gemessen wird alle fünf Sekunden,
dieselbe Abfrage wie für die Kacheln; eine halbe Stunde wird behalten, auf dem Server nichts
gespeichert.
