# Changelog

## v10

**Türkçe**

- Pencereyi kapatınca uygulama tepsiye düşüyor, idle durmuyor. Tepsiden göster, idle’ı durdur veya çık. Windows açılışında `--tray` ile küçültülmüş başlatılabiliyor. Aynı anda ikinci kopya açılmıyor.
- Idle süreci kendi kendine ölürse 15 sn, 45 sn, sonra 2 / 5 / 10 dk bekleyip en fazla 5 kez yeniden deniyor. DLL veya mimari hatasında denemiyor.
- Kart düşünce Windows bildirimi ve ses. Ayarlardan kapatılır.
- Genel bakış: çalışan oyunlar, bugün / oturum / kalan kart, son düşenler.
- İstersen günlük dosyası: `%LocalAppData%\FarmPhantom\logs\farmphantom-YYYYMMDD.log`, 14 gün tutuluyor. Bot, Presence, idle start/stop, kart düşüşü, beklenmeyen hatalar. Çerez ve anahtarlar logda maskeleniyor. Ayarlar → Tanılama.
- Oyun kapakları ve kart resimleri listelerde görünüyor. Önbellek: `%LocalAppData%\FarmPhantom\imgcache`. Paralel idle’da canlı şeritte her çalışan oyunun küçük kapağı var.
- Açılışta kısa splash (logo, halka, bar). Tıkla veya bir tuşa bas, geçer.
- Discord botu ayrı Docker işi değil; uygulama ile birlikte çalışıyor. Token’ı Ayarlar’dan giriyorsun, bu PC’de şifreli duruyor.
- Idle sırasında Discord Rich Presence: “FarmPhantom oynuyor” + logo. `discord-assets/logo.png` dosyasını Developer Portal’da `logo` adıyla yüklemen lazım.
- Pencereyi küçültmek görev çubuğunda bırakır; sistem tepsisi seçeneği yalnızca pencereyi kapatınca devreye girer.
- Envanter sorguları HTTP yanıtını akış halinde okur, yinelenen sayfa imlecini algılar; geçici I/O hataları kart izlemeyi durdurmaz.
- Steam callback döngüsü 100 ms yerine 250 ms’de bir uyanır; idle döngüsü uyanma sayısı %60 azalır.
- Sabit Steam sayfası ve doğrulama regex’leri kaynak üreteciyle derlenir.
- Resmi Steamworks DLL’i dağıtım izni varsa paketleme seçeneğiyle setup/ZIP’e eklenebilir; varsayılan olarak hariçtir.

**English**

- Closing the window sends the app to the tray; idle keeps running. Tray menu: show, stop idle, quit. Can start with Windows minimized (`--tray`). Only one instance.
- If the idle process dies, it retries up to 5 times (15s, 45s, then 2 / 5 / 10 min). DLL / arch errors are not retried.
- Windows toast + sound on a card drop. Can be turned off in Settings.
- Overview page: running games, today / session / remaining cards, recent drops.
- Optional log file: `%LocalAppData%\FarmPhantom\logs\farmphantom-YYYYMMDD.log`, kept 14 days. Bot, Presence, idle start/stop, drops, unexpected errors. Cookies and keys are masked. Settings → Diagnostics.
- Game covers and card images in the lists. Cache: `%LocalAppData%\FarmPhantom\imgcache`. Live strip shows a small cover for every running game.
- Short splash on launch. Click or press a key to skip.
- Discord bot runs inside the desktop app (no Docker). Token goes in Settings and stays encrypted on this PC.
- Rich Presence while idling (“Playing FarmPhantom” + logo). Upload `discord-assets/logo.png` in the Developer Portal as `logo`.
- Minimizing the window now keeps it on the taskbar; the tray option applies only when closing the window.
- Inventory polling streams HTTP responses and detects repeated page cursors; transient I/O failures no longer terminate drop monitoring.
- Steam callback polling now wakes every 250 ms instead of 100 ms, reducing idle-loop wakeups by 60%.
- Static Steam page and validation regexes are source-generated to reduce runtime compilation overhead.
- The official Steamworks DLL can be included in setup/ZIP when redistribution is authorized; it is excluded by default.

## v9

**Türkçe**

- Giriş artık sadece çerez. SteamID64 ve Web API anahtarı kalktı. Envanter de çerezle okunuyor (gizli envanter dahil).
- 1–32 oyunu aynı anda idle. Kuyruk, “aynı anda oyun” kadar yuvayı dolduruyor.
- Tek envanter okumasıyla bütün idle oyunlar izleniyor. Düşüş sayımı yalnız o oyunlara yazılıyor.
- Rozet sayfası her zaman kontrol ediliyor. Kalan sıfırsa oyun hemen bitiyor.
- Fiyat tabanı alıcının ödediği değil, satıcının neti.
- Ayarlar combo index değil, gerçek değer (dakika, para birimi kodu).
- Diske `badges-sample.txt` yazan debug kodu silindi.
- Discord: Rich Presence, uygulama içi “Discord’a katıl”, isteğe bağlı kişisel bot (DM + uzaktan komut).
- MIT lisans, üçüncü taraf listesi, `.gitignore`. `steam_api64.dll` repodan çıktı. Logo 512 px.

**English**

- Cookie-only login. SteamID64 and Web API key fields are gone. Inventory uses cookies too (including private).
- Idle 1–32 games at once. The queue fills as many slots as “games at once”.
- One inventory poll covers every running idle. Only those games count as drops.
- Badge page is always checked. Zero remaining finishes the game immediately.
- Price floor is seller net, not buyer-pays.
- Settings store real values (minutes, currency code), not combo indexes.
- Debug dump to `badges-sample.txt` removed.
- Discord: Rich Presence, in-app join button, optional personal bot (DMs + remote commands).
- MIT license, third-party notes, `.gitignore`. `steam_api64.dll` is not in the repo. Logo resized to 512 px.
