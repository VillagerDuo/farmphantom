# Diller / Languages

Bu klasördeki her `*.json` bir dil paketi. Ayarlar’daki dil listesi buradan doluyor.

Şu an: Türkçe, İngilizce, Almanca, Fransızca, İspanyolca, İtalyanca, Brezilya Portekizcesi, Rusça, Basitleştirilmiş Çince, Japonca, Korece, Arapça. Ana ekranlar 12 dilde var. Türkçe ve İngilizce çalışma zamanı mesajlarını da kapsıyor; diğerlerinde eksik satır İngilizceye (sonra Türkçeye) düşer.

Yeni dil: `language-template.json.txt` dosyasını örn. `nl-NL.json` diye kopyala. Culture ve görünen adı doldur, `strings` değerlerini çevir, **key’lere dokunma**. Paketi ekledikten sonra uygulamayı bir kez kapatıp aç. Kurulu diller arasında geçiş anında olur, seçim kaydedilir.

Arapça gibi RTL dillerde layout kendiliğinden tersine döner.

---

Every `*.json` here is a language pack. Copy `language-template.json.txt` to something like `nl-NL.json`, fill culture + native name, translate `strings`, leave keys as-is. Restart once after adding a pack. Switching among installed packs is instant. RTL (Arabic) flips layout automatically.
