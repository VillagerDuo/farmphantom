# GitHub’a koyarken / Publishing to GitHub

Kısa checklist. Hem ilk yayın hem sonraki sürümler.

## İlk kez

1. GitHub Desktop → File → Add local repository → bu klasör.
2. Henüz git yoksa Create a repository.
3. Gönderilecek dosyalara bak. `release/`, `dist/`, zip, `bin/`, `obj/`, `steam_api64.dll` listede olmasın.
4. Koda ve markdown’a Steam çerezi, bot tokenı, parola, kişisel veri yapıştırılmamış olsun.
5. `README.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` dursun.
6. İlk commit, sonra Publish repository. Private kutusunu kapatırsan herkese açık olur.
7. Vitrin sayfası: GitHub repo → **Settings → Pages** → Source: **Deploy from a branch** → Branch: `main`, folder: `/docs` → Save. Bir-iki dakika sonra `https://KULLANICI.github.io/FarmPhantom/` açılır. `docs/` klasörü bu iş için hazır.

## Yeni sürüm

1. `CHANGELOG.md` güncelle.
2. Yine DLL / zip / release klasörü commit etme.
3. Push.
4. Windows kurulum dosyası için Inno Setup 6 kur ve `.\scripts\Package-Windows.ps1 -IncludeInstaller -AppVersion 1.0.0` çalıştır. `dist/` içine setup ve ZIP çıkar. `steam_api64.dll`’i ancak bu dosyayı dağıtma yetkin varsa `app/` içine koyup `-IncludeLicensedSteamworksRuntime` seçeneğini kullan.
5. GitHub → Releases → Draft a new release. Tag örn. `v1.0.0`; `dist/FarmPhantom-Setup-win-x64.exe` dosyasını release’e ekle.
6. Exe/zip koyacaksan Valve’ın Steamworks dağıtım şartına bak. `steam_api64.dll` kaynak depoya konmaz; dağıtım yetkin yoksa setup’tan da hariç tut ve ilk açılışta kullanıcıdan seçmesini iste. Setup .NET’i kendi içinde taşır ama Steam istemcisini kurmaz.
7. Publish release.

## Not

`.gitignore` `release/`, `dist/`, `*.zip`, derleme çıktısı ve Steamworks DLL adlarını dışarıda bırakır. Daha önce yanlışlıkla eklenmiş bir dosyayı kendiliğinden silmez; ilk yayından önce listeyi gözden geçir.

Kaynak kodu açmak ile Windows setup/ZIP vermek ayrı işler. Paketleme komutu `README.md` içinde.

---

In GitHub Desktop: add this folder, check the file list (no `bin` / `obj` / `release` / zips / `steam_api64.dll`), make sure cookies and bot tokens are not in the tree, then publish public.

For later versions: update `CHANGELOG.md`, build the installer and zip with `.\scripts\Package-Windows.ps1 -IncludeInstaller -AppVersion <version>`, then attach them to a GitHub Release. Don’t put Valve’s `steam_api64.dll` in git. To include it in binary packages, confirm explicit redistribution rights, place it in `app/`, and pass `-IncludeLicensedSteamworksRuntime`; otherwise leave it excluded. The installer is self-contained for .NET but does not install Steam.
