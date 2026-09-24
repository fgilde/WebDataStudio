# Analyse

## Ausführungspläne

Das **Plan**-Panel zeichnet den Plan wie SSMS: von rechts nach links, von den Tabellen zum Ergebnis,
jeder Operator mit seinem Anteil an den Kosten, seinen Zeilen (tatsächlich von geschätzt, wenn der
Plan gelaufen ist) und seiner Zeit, und Pfeile so dick wie die Zeilen, die sie tragen. Ein Klick
auf einen Operator zeigt alles, was der Server dazu gemeldet hat – Prädikate, Ausgabespalten, die
Laufzeitzähler jedes Threads. Die Suche findet einen Operator oder eine Tabelle; Enter springt zum
nächsten.

Auf SQL Server ist der ganze Plan da, mit Memory Grant, Waits, Warnungen und den Indexen, die der
Server vermisst. **Save** schreibt ihn als `.sqlplan` oder `.xml`, die SSMS und Rider öffnen.
**Open plan** (der Ordner-Knopf, *Open execution plan* in der Befehlspalette oder eine aufs Panel
gezogene Datei) liest so eine Datei wieder ein – aus SSMS, von einem Kollegen, von letzter Woche –
in einen eigenen Tab, ohne Connection.

Geschätzt oder tatsächlich: ein tatsächlicher Plan führt das Statement aus. Sequenzielle Scans auf
großen Tabellen, fehlende Indizes und Auslagerungen auf Platte stehen unter **Findings**.

## Index-Berater

Aus Statement und Plan schlägt der Berater konkrete `CREATE INDEX`-Statements vor, mit der
Begründung, warum sie helfen sollten. Er liest die Prädikate, nicht nur die Tabellennamen, und sagt
es, wenn er allein aus dem SQL rät, weil kein Plan vorlag.

## Deep Analyze

Das Panel **Health** geht ein ganzes Schema durch und meldet fehlende, ungenutzte und doppelte
Indizes, nicht indizierte Fremdschlüssel, aufgeblähte Tabellen und veraltete Statistiken — mit dem
Statement zur Behebung, wo es eines gibt.

## Statistiken und Metriken

Tabellenstatistiken — Größe, Zeilenzahl, Indexgröße, letztes Vacuum oder Analyze — stehen im
Objekt-Detailpanel. Serverweite Metriken, Blockierketten und die Liste langsamer Abfragen liegen im
Panel **Administration**, für die Engines, die sie preisgeben: `pg_stat_statements` bei PostgreSQL,
der Query Store beim SQL Server, `performance_schema` bei MySQL.

Ist die Quelle nicht installiert, sagt das Panel das — statt eine leere Tabelle zu zeigen.
