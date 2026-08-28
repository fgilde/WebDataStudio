# Abfrage-Editor

![Abfrage-Editor](../../assets/screenshots/query-dark.png)

## Statements ausführen

- `F5` oder `Strg+Enter` führt die Markierung aus. Ohne Markierung läuft das Statement unter dem
  Cursor — und genau dieses Statement ist beim Tippen hervorgehoben, du siehst also immer, was
  laufen wird.
- `Strg+Umschalt+Enter` führt das ganze Skript aus; jedes Statement bekommt seinen eigenen
  Ergebnis-Tab.
- **Cancel** bricht ein laufendes Statement am Server ab, nicht nur im Browser.

## Bevor ein Statement läuft

Das Studio liest ein Statement, bevor es es ausführt, und sagt, was ihm aufgefallen ist:

- ein `UPDATE` oder `DELETE` ohne `WHERE` — also alle Zeilen
- ein `WHERE`, das immer wahr ist und damit nichts filtert
- `= NULL`, das nie wahr ist
- `TRUNCATE` und `DROP`, und was sie mitnehmen
- ein versehentliches Kreuzprodukt: ein `FROM` mit Kommas und nichts, was verbindet, oder ein `JOIN`
  ohne `ON` — `CROSS JOIN` sagt es absichtlich und bleibt unangetastet

Es warnt und verweigert nie. Jeder dieser Punkte kann genau so gemeint sein, und ein Studio, das sie
blockiert, bringt Leute nur dazu, die Prüfung zu umgehen — also sagt der Dialog, was er gesehen hat,
und der andere Knopf führt es trotzdem aus. In den Einstellungen lässt sich das Lesen abschalten.

Die Prüfung ist lexikalisch und kein Parser; Kommentare und Zeichenketten werden vorher geleert: ein
`-- DELETE FROM orders` ist ein Kommentar, und ein `WHERE` in einem Literal ist keine Klausel.

## Eine Transaktion

Der Schalter **single transaction** in der Werkzeugleiste legt eine Transaktion um das ganze
Skript: Commit, wenn jedes Statement erfolgreich war, Rollback beim ersten Fehler. Ausgeschaltet
committet jedes Statement für sich, so wie die Engine es standardmäßig tut.

## Eine Transaktion offen halten

Der Schalter oben deckt ein Skript ab. **Begin** deckt den anderen Fall ab — den, in dem man sehen
will, was ein Statement getan hat, *bevor* man sich entscheidet, es zu behalten.

Auf **Begin** hält der Tab eine Transaktion auf einer eigenen Sitzung offen. Alles, was der Tab von
da an ausführt, passiert darin: die Zeilen sind für dich geändert und für sonst niemanden, eine
zweite Verbindung sieht weiterhin die alten. Die Werkzeugleiste zeigt `transaction · n run`, solange
sie offen ist; **Commit** oder **Rollback** beenden sie. Das ist der Sicherheitsgurt für ein `UPDATE`
ohne `WHERE`: ausführen, ansehen, was zurückkam, und zurückrollen, wenn die Zahl nicht stimmt.

Drei Dinge, die man wissen sollte:

- Die Transaktion hält eine Sitzung aus dem Pool fern, solange sie lebt, und sie hält die Sperren,
  die ihre Statements genommen haben. Eine Transaktion, die niemand anfasst, rollt der Server nach
  fünfzehn Minuten zurück — `WDS_TRANSACTION_IDLE_SECONDS` verschiebt diese Grenze. Ein einfach
  geschlossener Browser endet genauso, und das ist besser als Sperren, die niemand findet.
- Den Browser-Tab zu schließen, während eine offen ist, fragt vorher nach.
- Engines ohne Transaktionen haben keinen Begin-Knopf: MongoDB, Redis und Objektspeicher.

## Nach einem Fehler weitermachen

**keep going on error** führt den Rest des Skripts auch dann aus, wenn ein Statement scheitert, und
meldet jeden Fehler dort, wo er passiert ist. Aus — die Voreinstellung — hört beim ersten Fehler
auf, was eine Migration will. An ist für das Skript mit hundert Inserts, bei dem zwei Duplikate
nicht die anderen achtundneunzig kosten sollen.

Innerhalb einer Transaktion gilt es nicht: ein gescheitertes Statement vergiftet die Transaktion bei
den meisten Engines, PostgreSQL lehnt danach ohnehin alles ab — eine Transaktion hört deshalb immer
auf.

## Parameter

Bind-Variablen so schreiben, wie die Engine sie kennt — `:name` bei PostgreSQL und Oracle, `@name`
beim SQL Server und MySQL, `$name` bei SQLite — und vor der Ausführung fragt ein Dialog nach den
Werten. Die Werte gehen als Parameter mit, nie in den SQL-Text hinein, und die letzten Werte merkt
sich der Tab.

Ein `::text`-Cast, ein `@@version` und ein Doppelpunkt in einer Zeichenkette sind keine Parameter
und bleiben unangetastet.

## Vervollständigung, Hover, Gehe zu Definition

Die Vervollständigung kennt das Schema der Verbindung, an der der Tab hängt: Tabellen, Spalten
hinter einem Alias, Schlüsselwörter und Snippets. Der Hover über einem Tabellennamen listet die
Spalten; `F12` öffnet das Objekt im Explorer.

## Snippets

Präfix tippen und `Strg+Leertaste`: `sel`, `ins`, `upd`, `del`, `join`, `cte`, `idx`, `cnt` sind
eingebaut. **Manage snippets** in der Befehlspalette öffnet einen Editor für eigene, die
serverseitig liegen und mit dem Arbeitsbereich mitwandern. Ein eigenes Snippet mit dem Präfix eines
eingebauten ersetzt dieses.

## Gespeicherte Abfragen und Verlauf

Jeder Lauf landet in einem durchsuchbaren Verlauf, der einen Neustart übersteht. Das Panel **Saved**
hält benannte Abfragen in Ordnern; beim Speichern wird das SQL des aktuellen Tabs vorgeschlagen und
seine Verbindung gemerkt.

## Was dieses Studio ausgeführt hat

Der Verlauf beantwortet „was habe ich ausgeführt“; das Panel **Statistik** daneben beantwortet „was
führe ich immer wieder aus, und wird es langsamer“. Statements werden nach Form gruppiert, nicht nach
Text: Kommentare, Zeichenketten, Zahlen und Parameterlisten werden zu `?`, dieselbe Abfrage mit
anderen Parametern ist also eine Zeile.

Jede Zeile trägt, wie oft sie lief, den schnellsten, den mittleren und den langsamsten Lauf, wie viele
Zeilen zurückkamen, wie oft sie fehlschlug — und einen Trend, der die erste Hälfte des Zeitraums mit
der zweiten vergleicht. „Langsamer als vorher“ ist der Satz, den man braucht; ein Mittelwert über
einen Monat ist es nicht.

Gelesen wird der eigene Verlauf des Studios, es geht also um das, was **hier** ausgeführt wurde — die
Statistiken der Engine selbst (`pg_stat_statements` und Äquivalente) stehen im Tab *Slow queries* der
Administration und sehen alles, auch das, was eine Anwendung ausgeführt hat.

## Formatierung

`Strg+Umschalt+F` formatiert den Puffer im Dialekt der Verbindung.
