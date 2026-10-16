# Üçüncü taraf / Third-party

Sürüm ve lisans metinlerini yayın öncesi NuGet / proje sayfalarından bir kez daha kontrol et.

| Paket | Ne için | Lisans |
|---|---|---|
| Avalonia | Arayüz | MIT |
| Steamworks.NET | Steamworks sarmalayıcı (idle) | MIT |
| DiscordRichPresence (Lachee) | Rich Presence | MIT |
| Discord.Net | Uygulama içi Discord botu | MIT |

`steam_api64.dll` (Linux/macOS karşılıkları da) Valve Steamworks SDK. Bu repoda yok. Sen indirip `app/` içine koyuyorsun. Steamworks SDK Access Agreement geçerli.

MIT paketlerinin telif + lisans metni kendi depolarında. Exe dağıtıyorsan bu dosyayı da pakete koy.

---

Double-check versions and licenses on each package page before you ship.

`steam_api64.dll` is Valve’s, not in this repo. Users drop it into `app/` themselves.

If you ship a binary, include this file next to the exe.
