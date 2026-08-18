# Verbindungen

## In der Oberfläche anlegen

Oben **Connections** öffnen, **Add** drücken und entweder das Formular ausfüllen oder eine
Verbindungszeichenfolge einfügen — beim Einfügen wird die Engine erkannt und der Rest gefüllt.
**Test** öffnet die Verbindung einmal und meldet, was der Server gesagt hat, ohne etwas zu
speichern.

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
