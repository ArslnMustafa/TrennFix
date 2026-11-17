# TrennFix – Rechnungseingang

Windows-Programm (eine einzige `TrennFix.exe`), das Rechnungen aus dem Postfach holt, sie nach Lieferant
und Jahr ablegt und alle wichtigen Angaben in einer Liste zeigt.

**Ein Klick auf „Rechnungen abrufen“** durchsucht alle Mails seit dem letzten Abruf (inkl. Unterordner, d. h.
auch Mails, die schon einsortiert wurden), und für jede Rechnung im Anhang wird Folgendes gemacht:

1. **Lesen**
   * **E-Rechnung** (XRechnung CII/UBL, ZUGFeRD/Factur-X): Werte werden exakt aus dem XML gelesen
   * **normales PDF**: Werte kommen aus dem Text des PDFs
   * **gescanntes PDF**: Texterkennung (OCR) mit der in Windows eingebauten OCR, ohne Zusatzinstallation
2. **Erkennen**: Rechnungsnummer, Datum, Fälligkeit, Netto/MwSt/Brutto, **Zahlungsart** (Überweisung /
   Lastschrift / bereits bezahlt / Kreditkarte), IBAN/BIC, Mandatsreferenz, Skonto, USt-IdNr., Lieferant,
   Dokumenttyp (Rechnung / Gutschrift / Mahnung; AGB und Lieferscheine werden ignoriert)
3. **Ablegen**: `Archiv\<Lieferant>\<Jahr>\2026-11-03_RE-12345.pdf` (+ XML + Original-Mail)
4. **Prüfen**
   * Doppelte Rechnungen werden erkannt
   * **Neue/geänderte IBAN eines bekannten Lieferanten** löst eine Warnung aus (Schutz vor Rechnungsbetrug)
   * Fehlende Werte werden gelb markiert

Danach: Drucken, als bezahlt markieren, Excel-Export, Filter „Offen – Überweisung“ mit Summe.

## Postfach

| Option | Wann |
|---|---|
| Outlook (Desktop) | Klassisches Outlook ist auf dem PC installiert; funktioniert mit jedem Kontotyp |
| Microsoft 365 | Exchange Online. Funktioniert auch ohne Outlook und mit dem „neuen Outlook“ (benötigt einmalig eine App-Registrierung mit `Mail.Read`) |
| IMAP | IONOS, Strato, GMX … |

Das „neue Outlook“ bietet keine COM-Schnittstelle mehr; dort bitte Microsoft 365 oder IMAP wählen.

## Projektstruktur

```
src/TrennFix.Core     Erkennung (XML / PDF-Text / OCR-Text), Archiv, SQLite-Index, Abruf-Logik, Excel-Export
src/TrennFix.Mail     IMAP (MailKit) und Microsoft 365 (Graph + MSAL)
src/TrennFix.App      WPF-Oberfläche, Outlook-COM, Windows-OCR, DPAPI-Passwortschutz
tests/                Tests mit Beispielrechnungen (inkl. echtem OCR-Durchlauf mit Tesseract)
```

## Bauen

```bash
dotnet test tests/TrennFix.Core.Tests
```

```bash
dotnet publish src/TrennFix.App -c Release -o publish
```

Ergebnis: `publish/TrennFix.exe` (self-contained, keine .NET-Installation nötig). Der GitHub-Workflow
`build` erzeugt dieselbe Datei als Artefakt `TrennFix-win-x64`.

## Daten

* Einstellungen: `%APPDATA%\TrennFix\settings.json` (IMAP-Passwort per Windows-DPAPI verschlüsselt)
* Index: `trennfix.db` im Archivordner (Archiv und Index gehören zusammen)
* Abgelegte Dateien werden nie verändert (GoBD); Aufbewahrungsfrist für Buchungsbelege: 8 Jahre
