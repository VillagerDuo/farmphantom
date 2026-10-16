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
4. GitHub → Releases → Draft a new release. Tag örn. `v1.0.0`.
5. Exe/zip koyacaksan Valve’ın Steamworks dağıtım şartına bak. `steam_api64.dll` bu git reposuna girmez.
6. Publish release.

## Not

`.gitignore` `release/`, `dist/`, `*.zip`, derleme çıktısı ve Steamworks DLL adlarını dışarıda bırakır. Daha önce yanlışlıkla eklenmiş bir dosyayı kendiliğinden silmez; ilk yayından önce listeyi gözden geçir.

Kaynak kodu açmak ile Windows zip’i vermek ayrı işler. Zip için `README.md` içindeki paketleme satırına bak.

---

In GitHub Desktop: add this folder, check the file list (no `bin` / `obj` / `release` / zips / `steam_api64.dll`), make sure cookies and bot tokens are not in the tree, then publish public.

For later versions: update `CHANGELOG.md`, push, draft a GitHub Release. Don’t put Valve’s `steam_api64.dll` in git. Shipping a binary zip is separate from publishing source; the README has the pack script.
