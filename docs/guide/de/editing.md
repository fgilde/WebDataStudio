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

Sie sind nicht bearbeitbar, und der Tab sagt den Grund: ohne Schlüssel gibt es keinen sicheren Weg,
eine einzelne Zeile anzusprechen. Also einen Schlüssel anlegen — oder über ein selbst geschriebenes
Statement arbeiten.

## Massenänderung

Zellen markieren, und **Bulk update** wendet einen Wert oder einen kleinen Ausdruck auf die
Markierung an — die spaltenweite Änderung, die sonst ein handgeschriebenes `UPDATE` wäre.
