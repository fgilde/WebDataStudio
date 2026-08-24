# Erste Schritte

## Container starten

```bash
docker run -d --name studio -p 8080:8080 -v wds-data:/data \
  -e WDS_CONN_SHOP="postgres://app:pw@db:5432/shop" \
  ghcr.io/fgilde/webdatastudio
```

Das Volume hält die Anwendungsdatenbank: in der Oberfläche angelegte Verbindungen, Abfrageverlauf,
gespeicherte Abfragen, Snippets und Layouts. Verbindungen aus Umgebungsvariablen werden bei jedem
Start neu gelesen und landen dort nie.

## Mit Docker Compose

```yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_PASSWORD: pw
      POSTGRES_DB: shop

  studio:
    image: ghcr.io/fgilde/webdatastudio
    ports: ["8080:8080"]
    volumes: ["wds-data:/data"]
    environment:
      WDS_CONN_SHOP: postgres://postgres:pw@db:5432/shop

volumes:
  wds-data:
```

## Mit .NET Aspire

```csharp
var db = builder.AddPostgres("db").AddDatabase("shop");

builder.AddContainer("studio", "ghcr.io/fgilde/webdatastudio")
       .WithHttpEndpoint(port: 8080, targetPort: 8080)
       .WithEnvironment("WDS_CONN_SHOP", db.Resource.ConnectionStringExpression)
       .WithVolume("wds-data", "/data");
```

### Mit dem Aspire-Integrationspaket

[Nextended.Aspire.Hosting.WebDataStudio](https://www.nuget.org/packages/Nextended.Aspire.Hosting.WebDataStudio/)
macht daraus einen Aufruf pro Datenbank und erkennt die Engine an der Ressource:

```csharp
builder.AddPostgres("pg").AddDatabase("shop").WithWebDataStudio();
builder.AddSqlServer("sql").AddDatabase("orders").WithWebDataStudio();
builder.AddRedis("cache").WithWebDataStudio();
```

Alle drei landen in einem Studio. `studioName:` ergibt ein zweites, und wer es selbst mit
`AddWebDataStudio` baut, setzt Login, Nur-Lese-Modus und Zeilenlimits gleich im App-Host.

## Als Desktop-Anwendung

Den Build für deine Plattform von der
[Releases-Seite](https://github.com/fgilde/WebDataStudio/releases) laden, entpacken und starten.
Er bedient <http://localhost:8080>, öffnet den Browser und legt seine Daten in einem Ordner `data`
neben der Datei ab.

## Als Desktop-Anwendung

Den Build für die eigene Plattform von der
[Releases-Seite](https://github.com/fgilde/WebDataStudio/releases) laden, entpacken, starten. Das
Studio läuft dann auf <http://localhost:8080> und öffnet sich **in einem eigenen Fenster** — ohne
Adressleiste, ohne Tabs, mit Icon in der Taskleiste wie jede andere Anwendung. Die Daten liegen in
einem Ordner `data` neben der Binärdatei.

Das Fenster kommt von der Plattform selbst: WebView2 unter Windows, WKWebView unter macOS,
WebKitGTK unter Linux. Das sind Teile des Systems, nicht des Downloads — deshalb bleibt der Download
eine Datei, und deshalb kann eines davon fehlen. Linux braucht dafür `libwebkit2gtk` aus der
Paketverwaltung.

Öffnet das Fenster nicht oder bleibt es leer, schreibt das Studio das ins Log und weicht aus: erst
auf ein installiertes Chromium (Edge, Chrome, Brave) im App-Modus — dasselbe Fenster ohne
Adressleiste — und dann auf einen normalen Tab. Zu sehen ist am Ende immer das Studio; nur der Rahmen
darum wechselt. `WDS_APP_WINDOW=false` erzwingt den Tab, `WDS_OPEN_BROWSER=false` öffnet gar nichts.

## Aus dem Browser installieren

Ein Studio, das offen ist — der Container im Netz, das Deployment einer Kollegin, der Desktop-Build
— lässt sich ohne Download als App installieren: **WebDataStudio installieren** in der Adresszeile
von Chrome oder Edge, oder *Als App installieren* im Browsermenü. Das ist dasselbe Fenster ohne
Adressleiste, mit eigenem Icon, und es zeigt weiter auf das Studio, aus dem es installiert wurde.

Zwischengespeichert wird nichts: das Studio liest lebende Datenbanken, und eine gecachte Antwort wäre
eine Lüge über deren Inhalt. Das Installieren ändert das Aussehen, nicht das Wissen. Über einfaches
HTTP jenseits von `localhost` bieten Browser das nicht an — dafür braucht es HTTPS.

## Erste Abfrage

1. Links im Explorer eine Verbindung wählen. Sie klappt in Schemas, Tabellen und Views auf.
2. Oben im Explorer auf **New query** drücken oder `Strg+N`.
3. Ein Statement tippen und `F5` drücken. Mit markiertem Text läuft nur die Markierung; ohne
   Markierung das Statement unter dem Cursor.
4. Das Ergebnis erscheint unter dem Editor, noch während die Abfrage läuft.

## Anmeldung

Sind `WDS_USER` und `WDS_PASSWORD` gesetzt, fragt die Anwendung einmal danach und merkt sich die
Sitzung in einem Cookie. Bleiben sie leer, gibt es gar keinen Login-Bildschirm — der sinnvolle
Standard für ein Studio, das ohnehin hinter dem eigenen Netz oder Proxy steht.
