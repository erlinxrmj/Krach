# Krach Soundboard

Windows-11-Soundboard für USB-Makropads (3×3 Tasten + 3 Drehregler) – entwickelt von **Erlin Ramaj**.

**Download:** rechts unter *Releases* → `Krach-Setup.exe` oder `Krach.exe`. Bedienung: siehe [ANLEITUNG.md](ANLEITUNG.md).

## Technik

- C# / .NET 8 / WPF, NAudio (+ NAudio.Vorbis)
- Geräteerkennung: Windows Raw Input (`RIDEV_INPUTSINK`), Identität = VID/PID aus dem HID-Pfad
- Blockieren nur des Makropads: Interception-Treiber direkt per `DeviceIoControl` (ohne interception.dll), Filter ausschließlich auf Geräte mit passender Hardware-ID
- Audio: WASAPI, zwei Ausgänge (Mithören + virtuelles Kabel), Mikrofon-Capture mit Live-Effekten (Pitch-Shift, Ringmodulation, Filter, Echo)
- Build: GitHub Actions → selbstständige Einzel-EXE (`PublishSingleFile`, self-contained) + Inno-Setup-Installer

## Selbst bauen

```
dotnet publish src/Krach/Krach.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Vorher das Symbol erzeugen: `python tools/make_icon.py` (braucht Pillow; macht der Workflow automatisch).
