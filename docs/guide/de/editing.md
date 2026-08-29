# Daten bearbeiten

Eine Tabelle im Explorer doppelt anklicken — der Daten-Tab ist bearbeitbar.

## So läuft es

- Doppelklick auf eine Zelle bearbeitet sie, `Enter` übernimmt, `Escape` verwirft. Boolesche Werte
  bekommen einen Schalter, eine Fremdschlüsselspalte eine Auswahlliste der referenzierten Werte.
- Zeilen einfügen, duplizieren und löschen über die Werkzeugleiste.
- Beim Tippen wird nichts gesendet. **Save** baut die Statements und zeigt sie.

## Die Vorschau ist nicht optional

Bevor sich eine einzige Zeile ändert, siehst du die genauen `INSERT`-, `UPDATE`- und
`DELETE`-Statements im Dialekt des Ziels. Anwenden führt sie in einer Transaktion aus, Abbrechen
wirft sie weg.

Die Vorschau hängt an einem Hash des Änderungssatzes. Hat sich der Änderungssatz zwischenzeitlich
bewegt, wird das Anwenden abgelehnt, statt etwas auszuführen, das du nicht gelesen hast.

## Tabellen ohne Primärschlüssel

Ein Schlüssel ist die erste Wahl. Fehlt er, spricht ein eindeutiger Index über Spalten, die nicht
`NULL` sein können, eine Zeile genauso eindeutig an — das Studio nutzt ihn.

Fehlt beides, bleibt die Antwort der Engine selbst auf „welche Zeile ist das": `ctid` in PostgreSQL,
`ROWID` in Oracle, `rowid` in SQLite. Das Studio liest sie zur Zeile mit, blendet sie im Grid aus
und schreibt `WHERE ctid = …` — damit ist die Heap-Tabelle, der nie jemand einen Schlüssel gegeben
hat, doch bearbeitbar.

Was das kostet, steht als Hinweis über dem Grid: **eine physische Adresse wandert, wenn die Zeile
geschrieben wird**, in PostgreSQL zusätzlich beim `VACUUM`. Also:

- Neu laden, bevor man weiterbearbeitet, wenn jemand anderes in die Tabelle geschrieben hat.
- Ein so gemachtes Update lässt sich nicht rückgängig machen. Die Adresse ist mit dem Schreiben
  gewandert; ein Undo über die alte träfe nichts — oder nach einem Vacuum irgendetwas anderes. Ein
  *Delete* lässt sich weiterhin zurücknehmen: die Zeile zurückzulegen braucht keine Adresse.

MySQL und SQL Server haben keine brauchbare Antwort — InnoDB behält seine Row-ID für sich, und
`%%physloc%%` ist undokumentiert und wandert. Dort sagt der Tab weiterhin, dass die Tabelle nicht
bearbeitbar ist, und meint es.

Alternativ: einen Schlüssel anlegen — oder über ein selbst geschriebenes
Statement arbeiten.

## Wie diese Zeile vorher aussah

Wo die Datenbank die Antwort selbst aufbewahrt hat, öffnet eine Uhr neben jeder Zeile ihre
Versionen: wann welche die Wahrheit war und was sich dazwischen geändert hat — die bewegten Spalten
sind hervorgehoben, die neueste trägt `now`.

Gelesen wird, was die *Datenbank* geschrieben hat, nicht, was das Studio gesehen hat:

| | |
|---|---|
| SQL Server | eine systemversionierte Tabelle (`SYSTEM_VERSIONING = ON`), gelesen mit `FOR SYSTEM_TIME ALL` |
| MariaDB | eine Tabelle `WITH SYSTEM VERSIONING`, genauso |
| Oracle | Flashback — wie weit es zurückreicht, hängt an der Undo-Retention des Servers, und das Panel sagt es |
| alles andere | nichts: PostgreSQL, MySQL, SQLite und der Rest führen keine Zeilenhistorie |

Die Uhr erscheint nur dort, wo sie etwas kann — die Tabelle hat Schlüsselspalten und die Engine hat
etwas aufbewahrt —, es gibt also keinen Knopf, der nach dem Klick „nicht unterstützt“ sagt.

Zweierlei ist es bewusst nicht. Es ist nicht der [Audit-Trail](administration.md): der weiß, was
durch *dieses Studio* ging, und die Zeile, die jemand aus einer Anwendung heraus geändert hat, ist
genau die, nach der gefragt wird. Und es ist kein Undo: die Versionen werden gelesen, nie
zurückgeschrieben. Einen Wert aus einer alten Version zu kopieren und ins Gitter zu setzen, ist eine
Änderung wie jede andere — mit derselben Vorschau.

## Eine Spalte, in der eine Datei steckt

Eine Binärspalte — `bytea`, `blob`, `varbinary`, `image` — zeigt, wie groß ihr Inhalt ist, und zwei
Knöpfe: Datei speichern oder durch eine andere ersetzen. Hex in eine Zelle zu tippen will niemand,
deshalb werden solche Zellen ausgewählt statt bearbeitet.

**Speichern** schreibt die Datei mit der Endung, die ihre ersten Bytes nennen: ein PDF kommt als
`.pdf` heraus, ein PNG als `.png`, und was das Studio nicht benennen kann als `.bin`. Vorher war es
immer `.txt` — eine Datei, die niemand öffnen kann.

**Ersetzen** nimmt eine Datei bis 8 MB — Hex verdoppelt die Größe auf dem Weg zum Server, und ein
Zellen-Editor ist nicht der Ort, um ein Video zu bewegen. Die Änderung geht durch dieselbe Vorschau
wie jede andere; dort steht der Wert als `0x89504e47… (12463 bytes)` statt als bildschirmfüllendes
Hex, und das Statement schreibt das Binärliteral der jeweiligen Engine (`0x…`, `'\x…'::bytea`,
`X'…'`), damit Bytes als Bytes ankommen.

## Zeilen aus der Zwischenablage

Der Zwischenablage-Knopf neben *Zeile einfügen* macht aus dem, was kopiert wurde — ein Block Zellen
aus Excel, ein paar Zeilen CSV, eine Auswahl aus einem anderen Grid — anstehende Inserts.

- **Tabulator oder Komma**, je nachdem, was in der ersten Zeile steht. Genau das kopiert eine
  Tabellenkalkulation, und genau das steht in einer CSV-Datei; beides muss niemand einstellen.
- **Eine Kopfzeile, aber nur eine echte.** Die erste Zeile gilt als Kopfzeile, wenn *jede* ihrer
  Zellen eine Spalte dieser Tabelle benennt; dann geht jeder Wert dorthin, wo sein Name hinzeigt,
  statt nach Position. `1,ada` sind Daten — `id,nonsense` ebenfalls.
- **Zitierte Zellen bleiben heil**: ein Komma in Anführungszeichen bleibt in der Zelle, `""` ist ein
  Anführungszeichen, und ein Zeilenumbruch innerhalb einer zitierten Zelle zerreißt die Zeile nicht.
- **Eine leere Zelle ist `NULL`**, nicht der leere String. Eine Tabellenkalkulation kann „null" nicht
  ausdrücken, und ein Leerfeld in einer Datumsspalte war noch nie `''`.

Durch Einfügen wird nichts geschrieben. Die Zeilen liegen als anstehende Inserts vor, genau wie
getippte — und die Vorschau des Änderungsskripts zeigt die `INSERT`-Anweisungen, bevor etwas läuft.

## Massenänderung

Zellen markieren, und **Bulk update** wendet einen Wert oder einen kleinen Ausdruck auf die
Markierung an — die spaltenweite Änderung, die sonst ein handgeschriebenes `UPDATE` wäre.

## Generierte Testzeilen

Der Zauberstab in der Werkzeugleiste des Daten-Tabs füllt eine Tabelle mit plausiblen Zeilen: eine
Spalte namens `name` bekommt Namen, eine E-Mail-Spalte Adressen, ein `varchar(6)` etwas, das in sechs
Zeichen passt. Die Strategie pro Spalte wird aus Name und Typ geraten und lässt sich im Dialog
korrigieren; derselbe Seed liefert dieselben Zeilen.

- **Fremdschlüssel zeigen auf existierende Zeilen** — bis zu 200 Schlüssel der Elterntabelle werden
  gelesen und daraus gewählt.
- **Was die Datenbank selbst füllt, wird übersprungen**: Identity, Serial, `AUTO_INCREMENT`, rowid.
- **Einen Typ, den der Generator nicht kennt, überlässt er der Datenbank**, statt einen Satz
  hineinzuschreiben. Ein Enum, eine Geometrie, ein Intervall: eine erfundene Zeichenkette lehnt die
  Engine ab, zu Recht — die Spalte wird also übersprungen, wenn sie NULL erlaubt oder einen
  Vorgabewert hat. Im Dialog lässt sich trotzdem eine Strategie wählen.

### Werte und ihre Typen

Jeder Wert einer Änderung oder generierten Zeile reist als Parameter, und ein Parameter reist als
Zeichenkette. Eine Zeichenkette ist kein Datum — deshalb sagt das Statement, was sie ist:
`CAST($1 AS date)`, mit dem deklarierten Typ der Spalte. PostgreSQL ist hier das strenge Gegenüber:
es lehnt `date = text` ab, statt zu raten. Das war der Grund für *„column signed_up is of type date
but expression is of type text"* bei einem generierten Datum.

Der Cast steht in der Vorschau, denn was man freigibt, muss das sein, was läuft. Binärspalten bleiben
unangetastet: eine Zeichenkette dort hineinzucasten schriebe Unsinn, wo ein Fehler ehrlicher ist.
