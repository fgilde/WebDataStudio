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

## Als Desktop-Anwendung

Den Build für deine Plattform von der
[Releases-Seite](https://github.com/fgilde/WebDataStudio/releases) laden, entpacken und starten.
Er bedient <http://localhost:8080>, öffnet den Browser und legt seine Daten in einem Ordner `data`
neben der Datei ab.

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
