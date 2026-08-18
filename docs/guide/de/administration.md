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
