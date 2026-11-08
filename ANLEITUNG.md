# Krach Soundboard – Anleitung

Entwickelt von Erlin Ramaj. Kostenlos, ohne Abo, ohne Cloud, ohne Registrierung.

## Installieren

1. `Krach-Setup.exe` starten → Weiter → Fertig. (Alternativ einfach `Krach.exe` starten – es ist keine Installation nötig.)
2. Falls Windows „Der Computer wurde durch Windows geschützt“ anzeigt: **Weitere Informationen → Trotzdem ausführen**. Das kommt, weil die App nicht kostenpflichtig signiert ist.

## In 4 Schritten loslegen

1. **Makropad wählen** – Seite „Makropad“. Dein Pad (VID 0x1189, PID 0x8840) wird automatisch erkannt. Sonst: „Per Tastendruck erkennen“ und eine Taste am Pad drücken.
2. **Tasten anlernen** – „Alle Tasten & Regler anlernen“ und einfach das Angezeigte drücken (9 Tasten, je Regler links/rechts/drücken).
3. **Sounds** – MP3/WAV/OGG direkt aus dem Explorer auf eine Taste im Soundboard ziehen.
4. **Discord** – Seite „Audio & Discord“ → „Automatisch einrichten“, danach in Discord als Eingabegerät **CABLE Output** wählen.

## Warum meine normale Tastatur sicher ist

- Krach unterscheidet Geräte über ihre feste USB-Kennung (VID/PID), nicht über die wechselnde Interception-Nummer.
- **Mit Interception** (bei dir installiert) wird nur das Makropad abgefangen. Deine Gaming-Tastatur läuft gar nicht durch Krach.
- **Ohne Interception** erkennt Krach das Pad per Windows Raw Input. Dann kommt die Pad-Taste (z. B. „C“) zusätzlich am PC an. Tipp: Pad-Tasten auf F13–F24 legen.
- **Notaus:** Knopf links unten oder **Strg + Alt + F12** schaltet sofort alle Eingabe-Hooks ab. Beim Beenden oder Absturz gibt Windows das Pad automatisch frei.

## Discord / Spiele

Windows kann einen Lautsprecher nicht als Mikrofon verwenden. Deshalb braucht es ein virtuelles Audiokabel – **VB-CABLE** ist kostenlos: https://vb-audio.com/Cable/ (installieren, PC neu starten).

Krach mischt dein echtes Mikrofon + Sounds (+ Stimmeffekt) in **CABLE Input**. In Discord:
- Eingabegerät: **CABLE Output (VB-Audio Virtual Cable)**
- Ausgabegerät: deine Kopfhörer
- Rauschunterdrückung: **Keine**, Echo-Unterdrückung aus (sonst filtert Discord die Sounds)

## Live-Stimmeffekte

Tief, Hoch, Roboter, Monster, Kind, Funk/Radio, Echo – an/aus und Wechsel auch per Drehregler. Wirken im Voice-Chat-Modus.

**Echte KI-Stimme (Klon):** kostenlosen *w-okada Voice Changer* mit RVC-Modell nutzen (braucht Grafikkarte). Dort Eingang = dein Mikrofon, Ausgang = ein **zweites** virtuelles Kabel (z. B. VB-CABLE A+B). In Krach bei „Dein echtes Mikrofon“ den Ausgang dieses zweiten Kabels wählen. Bitte keine fremden Stimmen nutzen, um dich als jemand anderes auszugeben.

## Probleme?

| Meldung | Lösung |
|---|---|
| „Ein anderes Programm benutzt Interception“ | HID-Monitor o. Ä. schließen → „Erneut versuchen“ |
| „Zugriff verweigert“ | „Als Administrator neu starten“ |
| Makropad nach Umstecken nicht blockiert | PC neu starten (Eigenheit des Interception-Treibers) |
| Freunde hören keine Sounds | Discord-Rauschunterdrückung auf „Keine“ |

Daten & Protokoll: `%AppData%\Krach`
