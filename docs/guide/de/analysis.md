# Analyse

## Ausführungspläne

Das Panel **Plan** erklärt das Statement im aktiven Abfrage-Tab, geschätzt oder tatsächlich. Der
Plan ist Baum und Grafik zugleich, mit einer Heatmap über die Kosten — der teure Knoten ist der,
den du zuerst siehst. Sequenzielle Scans auf großen Tabellen, fehlende Indizes und Auslagerungen
auf Platte werden benannt.

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
