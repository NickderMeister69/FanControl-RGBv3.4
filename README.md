# Nick Fan Control

Windows-App zur **Lüftersteuerung** und **RGB-Beleuchtung** – alles in einer App, ohne Zusatzprogramme.

## Download

Die fertige App: **`NickFanControl.exe`** (hier im Repository oder unter *Releases*).
Einfach starten und die Admin-Abfrage bestätigen. Einmalig vorher den Treiber
[PawnIO](https://pawnio.eu) installieren (für die Lüfterdrehzahl).

## Lüfter

Jeder Lüfter hat einen Modus:

| Modus | Was passiert |
|---|---|
| **Minimum** | leiseste Stufe, bei der der Lüfter noch dreht (wird automatisch gemessen) |
| **Manuell** | feste Leistung in Prozent |
| **Temperatur** | regelt nach der CPU-Temperatur („Leise bis“ … „Voll ab“) – ideal für den CPU-Lüfter |
| **Pumpe** | Wasserkühlungs-Pumpe auf festem Wert (Standard 30 %) |
| **BIOS** | das Mainboard regelt selbst |

- Ab einer einstellbaren Temperatur (Standard 70 °C) drehen auch die Minimum-Lüfter stufenlos hoch.
- An der Sicherheitsgrenze (Standard 85 °C) laufen alle geregelten Lüfter auf 100 %.
- Startet auf Wunsch mit Windows – nur als Symbol unten rechts in der Taskleiste.

## Beleuchtung (RGB)

Effekte: Statisch, Diashow, Regenbogen, Atmen, **Bildschirm** (reaktiv, wie Ambilight) und Aus –
alle Geräte synchron, mit Helligkeitsregler.

Erkennt automatisch:

- **Mainboards:** ASUS Aura, MSI Mystic Light, Gigabyte RGB Fusion 2, ASRock Polychrome
- **Lüfter-Hubs:** Corsair Lighting Node / Commander Pro / Commander Core, Lian Li Uni Hub, NZXT Hue 2 / RGB & Fan Controller
- **Razer:** Tastaturen, Mäuse, Mauspads, Kraken-Headsets, Chroma ARGB-Controller (ohne Synapse)

## Selbst bauen

Benötigt das .NET 8 SDK:

```
cd src
dotnet publish -c Release -o out
```

## Lizenz und Danksagung

Die Geräte-Protokolle und -Tabellen der RGB-Treiber basieren auf dem Projekt
[OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL-2.0).
Die Hardware-Sensoren liefert [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

Dieses Projekt steht daher unter der **GNU General Public License v2.0 oder später** – siehe [LICENSE](LICENSE).
