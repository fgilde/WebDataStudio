# Verbindungen

## In der Oberfläche anlegen

Oben **Connections** öffnen, **Add** drücken und entweder das Formular ausfüllen oder eine
Verbindungszeichenfolge einfügen — beim Einfügen wird die Engine erkannt und der Rest gefüllt.
**Test** öffnet die Verbindung einmal und meldet, was der Server gesagt hat, ohne etwas zu
speichern.

Beide Formen werden genommen: die providereigene Zeichenfolge,
`Host=db;Port=5432;Database=shop;Username=app;Password=pw`, und die URL,
`postgres://app:pw@db:5432/shop` — die das Studio in die erste übersetzt, bevor irgendein Treiber
sie sieht. Ein Passwort mit `#`, `?` oder Leerzeichen muss in keiner der beiden von Hand kodiert
werden.

## Eine Datenbankdatei

Nicht jede Datenbank ist ein Server. Eine SQLite-Datei, eine DuckDB-Datei oder ein Ordner mit
Parquet- und CSV-Dateien ist auch eine Datenbank, und das **Add**-Formular nimmt sie auf zwei Wege:

- **Upload** — Datei im Dateidialog auswählen. Das Studio legt sie unter seinem eigenen
  Datenverzeichnis ab, in einem Ordner mit dem Namen der Verbindung, und öffnet sie von dort. Das
  ist der Weg für die Datei auf dem Rechner vor dir, die der Container nicht sehen kann.
- **Browse the server** — durch die Ordner gehen, die das Studio lesen darf, und eine Datei
  auswählen, die schon da liegt. Das ist der Weg für eine gemountete Freigabe. Die Ordner sind das
  Datenverzeichnis des Studios plus alles, was `WDS_FILE_ROOTS` nennt; außerhalb davon ist nichts
  erreichbar.

Was womit geöffnet wird:

| Endung | Geöffnet als | Anmerkung |
| --- | --- | --- |
| `.db`, `.sqlite`, `.sqlite3`, `.db3`, `.s3db` | SQLite | Die Datei muss mit `SQLite format 3` beginnen — ein `.db` ist, was irgendwer umbenannt hat |
| `.duckdb`, `.ddb` | DuckDB | |
| `.parquet`, `.csv`, `.tsv`, `.ndjson`, `.jsonl`, `.json`, `.xlsx` (auch `.gz`, `.zst`) | Storage-Verbindung über den Ordner, in dem die Datei liegt | Immer nur lesend |

Eine Datendatei ist keine vierte Engine: das Studio liest einen Ordner voller Dateien längst über
DuckDB, und eine Datei ist ein Ordner mit einer Datei darin. Durch diese Verbindung kann nicht
geschrieben werden, egal was die übrigen Einstellungen sagen.

Zwei Formate werden abgelehnt, mit Begründung statt Schulterzucken:

- `.mdf` und `.ldf` — eine SQL-Server-Datendatei lässt sich nicht allein öffnen. Sie braucht einen
  laufenden SQL Server, der sie attacht; dort attachen und diesen Server als Verbindung hinzufügen.
- `.accdb` und `.mdb` — Access braucht den ACE-Treiber, der nur unter Windows und nur als eigene
  Installation existiert.

## Ein Link, der eine Verbindung öffnet

Ein Deployment kann dem Studio erlauben, Verbindungen aus seiner eigenen URL zu öffnen. Damit wird
es zu einer Art Live-Viewer für Datenbanken: jemandem einen Link schicken, und die Datenbank ist
offen, sobald die Seite geladen hat.

```
https://studio.example/?u=/data/shop.sqlite3
https://studio.example/?u=sales:/data/sales.duckdb,orders:/mnt/exports/orders.parquet
https://studio.example/?u=https://data.example/shop.sqlite3
https://studio.example/?u=shop:postgres%3A%2F%2Freader%3Apw%40db%3A5432%2Fshop
```

Ein `?u=` enthält eine kommagetrennte Liste. Jeder Eintrag ist ein Pfad, eine `http(s)`-URL oder
eine vollständige Verbindungszeichenfolge, optional mit `label:` davor, um die Verbindung zu
benennen. Ein Eintrag mit eigenen Kommas oder Semikola — eine Verbindungszeichenfolge,
`Server=host,1433` — muss prozentkodiert sein, sonst zerlegt das Komma ihn.

Standardmäßig ist das **aus**. `WDS_OPEN_FROM_URL` sagt, was ein Link mitbringen darf:

| Wert | Was erlaubt ist |
| --- | --- |
| `false` (Standard) | Nichts. `?u=` wird ignoriert, mit einer Zeile, welche Einstellung es erlauben würde |
| `true` | Pfad und Download — nie eine Verbindungszeichenfolge |
| `file` | Ein Pfad innerhalb der erlaubten Wurzeln |
| `download` | Eine `http(s)`-URL, und nur von den Hosts, die `WDS_OPEN_FROM_URL_HOSTS` nennt |
| `connection-string` | Eine vollständige Verbindungszeichenfolge, Zugangsdaten inklusive |
| `file,download` | Mehreres, kommagetrennt |

`connection-string` muss absichtlich genannt werden, und `true` lässt es bewusst weg: eine
Verbindungszeichenfolge in einer URL ist ein Passwort in der Browser-History, in Proxy-Logs und auf
Screenshots.

Ein Download wird einmal in das Datenverzeichnis des Studios geholt und dann als Datei geöffnet. Er
braucht `WDS_OPEN_FROM_URL_HOSTS` — ein Abrufer, der seine Adresse aus einem Link nimmt, ist ein Weg
zu den Adressen, die nur der Server erreicht — und er stoppt bei `WDS_OPEN_FROM_URL_MAX_MB` und
behält nichts, wenn eine Datei darüber hinausläuft. Weiterleitungen werden nicht gefolgt: eine
Weiterleitung ist ein zweiter Host.

Was ein Link öffnet, gehört dem Browser, der ihn geöffnet hat. Es ist im Baum als **from a link**
markiert, niemand sonst sieht es, und es ist weg, wenn der Prozess neu startet — nichts davon wird
aufgeschrieben, was genau der Sinn ist, wenn der Link ein Passwort enthielt.
`WDS_OPEN_FROM_URL_KEEP=store` ist der andere Zweig: die Verbindung wird wie jede andere im Store
gespeichert, übersteht Neustarts und ist für alle sichtbar. Richtig für ein Studio, das eine Person
betreibt, falsch für ein gemeinsames.

So geöffnete Verbindungen sind nur lesend, außer `WDS_OPEN_FROM_URL_WRITABLE=true`. Jeder Eintrag
antwortet für sich: ein Link mit drei Datenbanken öffnet die zwei, die er darf, und sagt in einer
Zeile, warum die dritte zu blieb. Der `u`-Parameter verschwindet aus der Adresszeile, sobald das
Studio ihn übergeben hat.

## Der Objektbaum

Eine Verbindung klappt in Schemas auf, dann Ordner, dann Objekte — und ein Objekt eine Ebene
weiter in seine Spalten, Indizes, Fremdschlüssel und Trigger, jeweils mit Typ oder den Spalten,
die es abdeckt, neben dem Namen.

Ein Rechtsklick zeigt genau das Menü, das zum Angeklickten passt: eine Tabelle bietet Daten,
Designer, Indizes, Skripte und Export; eine Spalte eine Abfrage darauf, einen Index darüber und ein
`DROP COLUMN`-Skript; ein Index Rebuild und Drop; ein Ordner eine neue Tabelle und Neuladen. Was
die Engine nicht kann, fehlt, statt kaputt angeboten zu werden.

Destruktive Statements landen im Abfrage-Tab statt im Menü zu laufen. Ausnahmen sind Datenbank
anlegen, Tabelle anlegen und Index ändern — die haben ihren eigenen Dialog, und jede
Schema-Änderung zeigt ihr SQL trotzdem vor der Ausführung.

### Azure SQL, Synapse und Fabric

Die Liste **Start from** im Formular trägt die Connection-Strings, die sich niemand merkt: Azure SQL
mit Managed Identity, mit dem eigenen Konto oder mit Entra-Passwort; ein Synapse-Pool, serverless oder
dediziert; ein Fabric-Warehouse; die Azure-Datenbankdienste. Ein Preset füllt den Connection-String,
danach ist er wie jeder andere editierbar.

Sagt die Verbindung, dass sich eine Person anmeldet — `Authentication="Active Directory Device Code
Flow"` oder `Interactive` —, versucht das Studio nicht, im Container einen Browser zu öffnen. Es führt
den Device-Code-Flow selbst: die Verbindungsliste markiert sie mit **sign-in**, das Schlüsselsymbol
öffnet einen Dialog mit einem Code, und den gibt man auf einem Gerät mit Browser ein. Das Token bleibt
danach im Speicher des Servers — nie auf Platte, nie im Browser — und die Verbindung öffnet damit, bis
es abläuft.

Eine Managed Identity braucht davon nichts und bleibt überall die bessere Antwort.

Ein Bucket ist auch eine Verbindung: `s3://`, `azblob://`, `gs://` und `file://` öffnen
Objektspeicher im gleichen Baum, wo eine Datei als Tabelle abgefragt werden kann — siehe
[Objektspeicher](storage.md).

## Einen Wert finden statt einer Tabelle

Das Filterfeld findet Objekte. **Find data** — die Lupe in der Werkzeugleiste des Explorers — findet
einen Wert: „welche Tabelle hat 4711 drin?“, serverseitig beantwortet, eine Abfrage je Tabelle und
damit je ein Scan.

Es ist typbewusst, und das hält es schnell: eine Zahl wird gegen numerische Spalten als Zahl
verglichen und in Text gesucht, ein Datum gegen Datumsspalten, und eine Spalte, die den Wert gar nicht
halten kann — `bytea`, Geometrie, ein Bild — wird nie nach Text gecastet. Text wird auf jeder Engine
ohne Groß- und Kleinschreibung verglichen, damit dieselbe Suche auf jeder Verbindung dieselben Zeilen
findet.

![Find data](../../assets/screenshots/datasearch-dark.png)

Das Ergebnis sagt, wo der Wert steht, in welcher Spalte und wie viele Zeilen ihn tragen — die meisten
Treffer zuerst. Ein Klick öffnet diese Tabelle, schon auf die passende Spalte gefiltert. Außerdem
sagt die Antwort, wie viele Tabellen durchsucht, wie viele übersprungen wurden und warum, und ob sie
am Tabellenlimit aufgehört hat.

## Ist der Server noch da?

Vor jeder Verbindung steht ein Punkt mit dem, was das Studio über sie weiß. Grau heißt „hat noch
niemand gefragt", grün steht für einen Server, der geantwortet hat, rot für einen, der es nicht tat.
Der Tooltip nennt die Dauer der Antwort — oder, wenn keine kam, woran es lag.

Das ist bewusst kein Polling. Ein Studio mit zehn Verbindungen würde zehn davon öffnen, manche durch
einen SSH-Tunnel, für eine Reihe Punkte, die gerade niemand ansieht. Stattdessen fragt es einmal,
wenn eine Verbindung aufgeklappt wird — in dem Moment, in dem jemand Interesse zeigt — und erneut
bei jedem Klick auf den Punkt.

Gemessen wird ein echter Round-Trip, nicht „ein Verbindungsobjekt aus dem Pool existiert": das
kleinste Statement, das die Engine kennt, mit Uhr. Ein grüner Punkt mit gelbem Ring heißt, der
Server hat geantwortet und dafür länger als eine Viertelsekunde gebraucht.

## Nur die Schemas, in denen gearbeitet wird

Ein Server mit fünftausend Tabellen lässt jedes Studio für alle bezahlen: die erste Ebene des Baums,
der Vervollständigungs-Cache, die Objektsuche und der Schema-Snapshot laufen jeweils ab, was sie
bekommen. **Eigenschaften…** einer Verbindung hat dafür die Auswahl **Schemas read** — zwei benennen,
und mehr wird nicht gelesen. Leer heißt alles, und das bleibt die Vorgabe.

Eine Bereitstellung kann es stattdessen festlegen: `WDS_CONN_<NAME>_SCHEMAS=public,sales`; die Auswahl
berichtet das dann, statt Bearbeitbarkeit vorzutäuschen. Gefiltert werden nur Schemas und Datenbanken —
ein Bucket, ein Keyspace oder ein Server-Ordner geht durch, denn ein Schema-Filter, der auf einer
anderen Engine den Baum leert, wäre ein Fehler.

## Ein Studio, zu dem jeder seine eigenen Daten mitbringt

Alles oben geht von einer Art Deployment aus: jemand schreibt die Verbindungen auf, ein Team öffnet
das Studio, alle sehen dieselben Datenbanken. Es gibt noch eine — ein Studio im offenen Internet als
Viewer, bei dem jeder Besucher seine eigene Datenbank mitbringt und die der anderen nicht sieht.
Zwei Einstellungen machen daraus dieses Studio, und beide ändern nichts, solange sie nicht gesetzt
sind.

**Wohin geht eine neue Verbindung?** `WDS_CONNECTION_SCOPE`:

| Wert | Bedeutung |
| --- | --- |
| `stored` (Standard) | In den Verbindungs-Store: aufgeschrieben, übersteht Neustarts, jeder, der sie sehen darf, sieht sie |
| `session` | In den Browser, der sie angelegt hat, und in keinen anderen. Nichts auf Platte, weg beim Neustart des Studios oder wenn die Sitzung abläuft |

Im `session`-Scope landen Formular, Upload, Import und ein `?u=`-Link am selben Ort: in der Liste
dieses Browsers. Die Verbindungsliste sagt das und bietet **Forget my connections** — ein Knopf für
alles Mitgebrachte, Dateien inklusive.

**Welche Wege hinein gibt es überhaupt?** Drei Schalter, alle an, solange ein Deployment nichts
anderes sagt:

| Variable | Die Tür, die sie schließt |
| --- | --- |
| `WDS_ALLOW_ADD_CONNECTION=false` | Das Formular. Auch Import und das Testen einer Verbindung — letzteres öffnet, was man ihm gibt, und behält nichts, was es zur billigsten Tür der vier macht |
| `WDS_ALLOW_FILE_UPLOAD=false` | Eine Datenbankdatei vom Rechner des Besuchers |
| `WDS_ALLOW_FILE_BROWSE=false` | Das Durchsuchen der Server-Ordner |

Eine geschlossene Tür nimmt ihren Knopf mit, statt einen zu zeigen, der mit einer Ablehnung
antwortet — und die Ablehnung, für alle, die die API direkt fragen, nennt die Einstellung, die es
erlauben würde.

**Ein Ordner mit Beispieldateien braucht einen offenen Browser.** `WDS_FILE_ROOTS` neben
`WDS_ALLOW_FILE_BROWSE=false` ist ein Ordner, den niemand per Klick erreicht: Besucher kommen dann
nur über einen Link `?u=/data/files/<Name>/<Datei>` daran. Bei einem Viewer, dessen ganzer Sinn
„hier gibt es was zu sehen" ist, lass den Browser an; bei einem im offenen Internet lass ihn zu und
verteile Links — eine Auflistung der Container-Ordner ist eine Beschreibung der Bereitstellung.

„Niemand darf etwas hinzufügen" sind diese drei auf `false`. Das ist auch allein nützlich: ein
Deployment, dessen Verbindungen aus einem App-Host kommen, schließt das Formular und lässt den Rest.

### Eine Sitzung, die endet

`WDS_SESSION_TTL_MINUTES` (Standard 240, `0` heißt nie) ist, wie lange eine Sitzung still sein darf,
bevor ihre Verbindungen und die Dateien dahinter verworfen werden; ein Sweeper läuft alle fünf
Minuten. `WDS_SESSION_MAX_CONNECTIONS` (25) ist, wie viele ein Browser gleichzeitig halten darf.

Das Cookie, das Besucher trennt, ist `HttpOnly` und über https `Secure`. Es ist keine
Sicherheitsgrenze: wer das Cookie eines anderen kopiert, bekommt dessen Verbindungen.

### Die Hosts, die das Studio erreichen darf

Ein Studio, in das jeder eine Verbindungszeichenfolge tippen darf, ist ein Verbinder nach außen von
dort, wo es läuft: ein Besucher erreicht damit Adressen, die nur der Server erreicht.
`WDS_CONNECT_HOSTS` — standardmäßig leer, also keine Einschränkung — ist eine kommagetrennte
Positivliste, gegen die jedes Verbindungsziel geprüft wird, egal woher die Verbindung kommt:
Formular, Test, Link, Store, Umgebung. `*.example.com` trifft eine Subdomain-Ebene, dieselbe Regel
wie bei der Download-Liste.

Eine Verbindung, deren Host nicht in der Liste steht, wird gar nicht angeboten — auch eine, die vor
dem Setzen der Liste aufgeschrieben wurde: eine Verbindung aus der Umgebung darf nicht der Weg daran
vorbei sein. Eine Verbindungszeichenfolge, aus der sich kein Host lesen lässt, wird bei gesetzter
Liste abgelehnt, denn Raten würde die Liste zu einem Vorschlag machen. Setze sie bei allem, was
Fremde erreichen können; siehe [Deployment](../deploy.md#exposure).

## Eigenschaften

**Properties…** auf einer Verbindung, ihrer Datenbank oder einem Schema zeigt, was diese Verbindung
ist: Name, Engine, wo sie definiert wurde, ob sie nur lesend ist, der SSH-Tunnel falls vorhanden —
und was der Server selbst meldet, etwa Version, aktuelle Datenbank, Kodierung, Zeitzone und Größe.
Unten steht, was die Engine unterstützt; eine fehlende Schaltfläche in der Oberfläche hat damit
einen sichtbaren Grund.

Auch die Verbindungszeichenfolge steht dort, das Passwort durch eine Maske ersetzt. Das Auge zeigt
es, und es gibt zwei Kopier-Schaltflächen: eine kopiert die Zeichenfolge ohne Passwort, die andere
mit. Das Passwort wird nur geholt, wenn eine davon gedrückt wird — es ist nie Teil eines normalen
Seitenaufbaus, und das Aufdecken übersteht das Schließen des Dialogs nicht.

Antwortet der Server nicht, zeigt der Dialog trotzdem die Definition und nennt den Fehler — meist
ist genau das der Grund, ihn zu öffnen.

## Gruppen und Farben

Eine Verbindung kann eine Gruppe und eine Farbe tragen. Der Explorer zeichnet Gruppen als
einklappbare Überschriften und färbt jede Verbindungszeile in ihrer Farbe. Rot für Produktion ist
eine Konvention, die sich lohnt.

## Nur-Lese-Verbindungen

Das Nur-Lese-Flag wird im Treiber geprüft: alles, was kein Lesen ist, wird mit klarer Meldung
abgelehnt, und die Oberfläche blendet die Aktionen aus, die scheitern würden. `WDS_READONLY=true`
erzwingt das für alle Verbindungen zugleich.

## SSH-Tunnel

Im Formular den Abschnitt **SSH tunnel** öffnen und Host, Benutzer sowie Passwort oder privaten
Schlüssel angeben. WebDataStudio öffnet den Tunnel, wenn eine Sitzung ihn braucht, teilt einen
Tunnel zwischen gleichzeitigen Sitzungen und schließt ihn eine Minute nach der letzten.

Host und Port der Datenbank in der Verbindungszeichenfolge bleiben so, wie der Jump-Host sie sieht
— genau dafür ist ein Tunnel da. Lässt sich der Tunnel nicht öffnen, nennt der Fehler SSH und nicht
ein allgemeines Timeout gegen einen Host, den du ohnehin nie direkt erreichen konntest.

## TLS

Der Abschnitt **TLS** schreibt den passenden Schlüssel in die Verbindungszeichenfolge der gewählten
Engine: `SSL Mode` bei PostgreSQL, `SslMode` bei MySQL, `Encrypt` beim SQL Server.
Client-Zertifikate werden über einen Pfad in der Verbindungszeichenfolge referenziert; die Dateien
müssen also für den Container erreichbar sein.

## Import und Export

`GET /api/connections/export` liefert die Definitionen ohne jedes Geheimnis: keine
Verbindungszeichenfolge, kein Passwort, kein Schlüssel. Der Import legt die Verbindungen mit Host
und Datenbank wieder an und lässt die Zugangsdaten leer — eine geteilte Datei kann also kein
Passwort verraten.

## Pooling

Sitzungen werden je Verbindung gepoolt. `WDS_MAX_SESSIONS` begrenzt, wie viele eine Verbindung
gleichzeitig halten darf, `WDS_IDLE_TIMEOUT_SECONDS` entscheidet, wann eine ungenutzte geschlossen
wird. Wird eine Verbindung geändert oder gelöscht, fallen ihre gepoolten Sitzungen sofort weg.
