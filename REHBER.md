# Metin2 Bot — Ne Yaptığı Hakkında Rehber

> Bu belge, `Metin2AutoFishCSharp.exe` programının **ne yaptığını**, **nasıl çalıştığını** ve
> kaynak koddaki modüllerin karşılıklarını anlatır. Sürüm: **1.0.2**

---

## 1. Genel Tanım

Program, Metin2 oyunu için geliştirilmiş bir **masaüstü otomasyon (macro) botudur**.
Oyunun **belleğine, process'ine veya dosyalarına hiçbir müdahalede bulunmaz**; tamamen
**ekran görüntüsü okuma + görüntü işleme + sahte mouse/klavye girdisi** mantığıyla çalışır.
Yani oyuncu gibi ekranı izler, oyuncu gibi tıklar/klavye basar.

Üç ana modu vardır ve **aynı anda yalnızca biri çalışabilir**:

| Mod | Ne yapar |
|---|---|
| **Fishing (Balık)** | Otomatik balık tutar, yakar, solucan/ Kamp Ateşi alır, ölüp dönünce devam eder |
| **Level and Farm (Kasma)** | HP/SP potu basar, statü dağıtır, becerileri süreyle kullanır, ETP toplar |
| **Enerji Kristali** | Simyacı görev döngüsünü kendisi çevirir (satın al → taşı → tekrarla) |

Yanında hep aktif olabilen destek sistemleri: **Chat/Fısıltı cevaplama**, **Telegram bildirimleri**,
**Zamanlayıcı (mola/durdurma)** ve **otomatik oyun yeniden giriş**.

---

## 2. Temel Çalışma Prensibi (Görüntü İşleme Zinciri)

```
┌─────────────────────┐    ┌──────────────────────────┐    ┌─────────────────────────┐
│ 1) EKRAN YAKALAMA   │ →  │ 2) GÖRÜNTÜ İŞLEME       │ →  │ 3) SAHTE GİRDİ          │
│ ScreenShotWinAPI    │    │ ImageProcess +           │    │ GameInputHandler +      │
│ GDI BitBlt ile      │    │ ImageObjects (şablon     │    │ mouse_event,            │
│ ekranın belirli     │    │ karşılaştırma, renk      │    │ SetCursorPos,           │
│ bölgeleri alınır   │    │ piksel eşleştirme)       │    │ SendInput (klavye)      │
└─────────────────────┘    └──────────────────────────┘    └─────────────────────────┘
```

1. **Ekran yakalama** — `Sources/ScreenShotWinAPI.cs`
   `gdi32!BitBlt` ile oyun penceresinin tanımlı bölgeleri (rect'ler) bitmap'e çevrilir.
   Bölgelerin hepsi `GameObjectCoordinates.cs` içinde sabit koordinatlar olarak tanımlıdır
   (envanter slotu, mini harita, chat satırı, ayarlar düğmesi, ölüm ekranı vb.).

2. **Görüntü işleme** — `Sources/ImageHandle/`
   - `ImageProcess.cs`: Şablonları ekran görüntüsüyle piksel piksel karşılaştırır
     (`FindImageOnScreen`, `FindAllImagesOnScreen`, `CompareBitmaps`, duyarlılık
     seviyeleri `SENSIBLITY_LOW/MED/HIGH` ile).
   - `ImageObjects.cs`: `Fishes/`, `Images/` klasörlerindeki PNG şablonlarını yükler;
     örn. balık ikonları, "oltaya bir şey takıldı" yazısı, giriş ekranı, ölüm ekranı.
   - `GameAlphabetDetecter.cs`: **Kendi OCR'ı.** `ChatResources/GameAlphabets/` içindeki
     harf/rakam PNG'leriyle chat satırlarındaki yazıyı harf harf okur.

3. **Girdi simülasyonu** — `Sources/Inputs/`
   - `GameInputHandler.cs`: `mouse_event` + `SetCursorPos` ile tıklama, sürükleme, sağ tık.
   - `KeyboardInput.cs`: `SendInput`/scan code ile tuş basma (ESC, Ctrl+O, F tuşları, 1-4 vb.).

> **Sonuç:** Bot "ekranı okuyup göz kararı değil, piksel eşleşmesine göre" karar verir;
> insansı rastgelelik (`TimerGame.SleepRandom`) ile gecikmeler ekleyip doğal davranmaya çalışır.

---

## 3. Thread (İş Parçacığı) Mimarisi

`Sources/Threads/ThreadsHandler.cs` başlatıldığında **3 thread** doğar:

| Thread | Görev | Kaynak |
|---|---|---|
| **Thread 1 — Ana döngü** | Aktif modu çalıştırır: `StartFishing()` / `StartLevelAndFarming()` / `StartEnergyCristal()`. Zamanlayıcı kontrolü: süre dolarsa oyunu ayarlar menüsünden kapatır ve botu durdurur. | `FishingHandle`, `LevelHandle`, `EnerjyCristalHandle`, `TimerGame` |
| **Thread 2 — Oyun durumu gözcüsü** | Sürekli şu ekranları kontrol eder: giriş ekranı, karakter seçim ekranı, **ölüm ekranı**, satım (sale) penceresi, ayarlar düğmesi (oyun açık mı). Oyun kapandıysa/metin2 ikonu kaybolduysa **otomatik yeniden başlatır + GameForge girişini yapar**. Telegram'a uyarı gönderir. | `GameHandler/CheckGameStatus.cs` |
| **Thread 3 — Yan işlemler** | Fısıltı cevaplama, chat cevaplama, ticaret paneli kapatma, mini haritada oyuncu tespiti (yavaş balık için), ETP toplama gibi işler. | `Chatting`, `WhispersHandle`, `PlayersHandler`, `CharPickUpItems` |

Tüm modüller arası durum, `ThreadGlobals.cs` içindeki **volatile bayraklarla** paylaşılır
(`isFishingStopped`, `isCharKilled`, `isSettingButtonSeemed`, `isTimerBreakEnabled`, ...).

**Çakışma kuralı:** Balık, Level ve Enerji modlarının start butonları birbirini kilitler;
bir mod açıkken diğerine basılırsa hata mesajı çıkar (bkz. `MainForm.cs`).

**Acil durdurma:** `Ctrl + O` kısayolu (global hotkey) veya Telegram'dan **"Durdur"**
mesajı, aktif olan modu tek tuşla durdurur.

---

## 4. Modüller Ne Yapıyor?

### 4.1 Fishing (Balık Botu)

**Hazırlık aşaması** — `Sources/GameHandler/PrepareFishing.cs`
- Balıkçı NPC'yi ekran görüntüsünden bulur (`FindFisher`).
- Gerekirse **solucan alır** ve slotlara dizer (`BuyFiftyWormAsNeeded` → en fazla 50).
- Balıkçıdan **Kamp Ateşi** alır (`BuyKampAtasiFromFisher`).
- Envanterdeki balıkları kamp ateşine sürükleyip **ızgarada yakar** (`GrillFishingHandle`).
- Balık tutma noktasına gider (`GoToFishPlace`), balıkçı sayfasını kapatır.

**Ana balık tutma döngüsü** — `Sources/GameHandler/FishingHandle.cs`
- Oltayı atar, takılma/çekilme sinyallerini **piksel renk değerleriyle** tespit eder
  (kod içinde `fishPixelOneValue` ... `fishPixelSeventeenthValue` renk listesi).
- Yakalanan balığı **hedef listesine göre** değerlendirir; işaretli balıkları tutar,
  işaretli değilse bırakır/yakar.
- **Hepsi** seçeneği: envanterdeki tüm objeleri ateşe sürükler (yanında sadece balıklar yanar).
- Ek özellikler:
  - **Adapte Tutma**: haritada/yakında oyuncu tespit edilirse daha yavaş çalışır
    (insan gibi görünmek için, bkz. `SleepRandomForPlayers`).
  - **Mini mola**: gerçekçilik için saniye cinsinden ufak molalar (`isFishingMiniBreakActive`).
  - **PC Yavaşsa**: yavaş bilgisayarlar için zamanlamaları gevşetir (`TimerGame.IS_PC_SLOW`).
- Karakter ölürse Thread 2 ölüm ekranını görür, çıkış atar ve **kanal değiştirerek devam eder**.

**Tutulabilen balıklar (checkbox'lar):** Yabbie, Palamut, Altın Sudak (GoldSudak),
Kurbağa, Kadife, Denizkızı anahtarı, Hepsi.

### 4.2 Level and Farm (Kasma)

`Sources/GameHandler/LevelHandle.cs` + `Sources/LevelAndFarms/`

- **HP/SP potu**: belirlediğiniz yüzde sınırın altına düşülünce pot basar
  (`ControlAndFillTheHpAndSp`, `HP_SP_RATE[0..1]`). Yüzdelikler trackbar/textbox ile girilir.
- **Statü dağıtımı** (`StatusHandler.cs`): 60 seviyeye kadar, sizin verdiğiniz
  **öncelik sırasına** göre (örn. `HP=4, SP=3, STR=2, DEX=1` → önce HP, sonra SP...)
  puanları dağıtır (`STATUS_PRIORITY[]`).
- **Beceriler** (`SkillsHandler.cs`): 1-2-3-4 ve F1-F2-F3-F4 tuşlarına atanan
  **süreler dolunca** tuşa basar (hedef şartı aramaz — pasif/uzaktan vuran becerilerle uyumlu).
- **Otomatik Av** (`AutoHunter.cs`): oyunun Otomatik Av penceresini algılayıp
  başlat/durdur (`StartStopOtomatikAv`).
- **ETP toplama**: "Yerden ETP görünce topla" işaretliyse, yakındaki düşen ETP'yi alır
  (`CharPickUpItems`, Thread 3 içinde `isETPPickUpActive` kontrolü).
- Hızlı erişim slotlarında pot yoksa, envanterde tespit edilen potları slota koyar
  (XXL potlar algılanmaz — README notu).
- Ölüm → çıkış → rastgele kanal değişimi ile devam.

### 4.3 Enerji Kristali

`Sources/GameHandler/EnerjyCristalHandle.cs` (720 satır)

**Ön koşullar (arayüzde de yazıyor):**
- Sadece **kırmızı bayrakta** çalıştırılmalı,
- Hesap **35+ level** olmalı,
- **Simyacıdan enerji kristali görevi** alınmış olmalı,
- Silah satıcısından yeni koku görevi alınmış (görevi yapmamış) olmalı.

**Döngü:**
1. Silah satıcısı sayfasını açıp gerekli malzemeyi satın alır (`BuyKediIsirigi`, `CheckSilahciShopPage`).
2. Envanterdeki malzemeyi simyacıya sürükler (`DragKediIsirigi`).
3. Süre bitince sonucu alır, döngüyü tekrarlar; takılırsa otomatik CH atar (README).

**Telegram uyarıları (varsa):**
- `"Karakterde para yok paraaaa"` — para bittiyse (`EnerjyCristalHandle.cs:591`)
- `"Karakterde envanter full dolu boşalt köle"` — yer yoksa (`EnerjyCristalHandle.cs:662`)

### 4.4 Chat ve Fısıltı Cevaplama

`Sources/ChatHandler/` — Kelime → Cevap eşleştirme sistemi.

- `GameAlphabetDetecter.cs`: Chat satırındaki yazıyı alfabe şablonlarıyla okur (OCR).
- `Chatting.cs`: Oyuncu tespit edilince (mini haritada veya ekranda) sohbeti başlatır;
  `WhispersHandle.cs`: Fısıltı gelip gelmediğini kontrol edip cevap yazar.
- `ChatFileHandler.cs`: Eşleşmeler `ChatResources/ChatQuestionAnswer/ChatQuestAnswer.txt`
  dosyasında saklanır; konuşmalar `RecordChats.txt` ve `RecordWhisperChat.txt`'ye kaydedilir.
- **Check Chat** butonu (MainForm) bu eşleşmeleri düzenlemeniz için dialog'u açar.

**Kullanım (README):** üst kısma tespit kelimeleri (virgülle): `ne yapıyorsun,ne yaptın`
→ alt kısma cevaplar: `iyidir senden ne haber,iyiyim balık tutuyorum`
→ *"verileri yükle"*.

### 4.5 Telegram Entegrasyonu

`Sources/TelegramBot.cs` — [Telegram.Bot](https://www.nuget.org/packages/Telegram.Bot) v19 kütüphanesi.

**Kurulum akışı:**
1. "Telegram Bot Aktif Et" checkbox'ını işaretle.
2. Botu Telegram'dan bulup **mesaj gönderin veya /start basın** (arayüzde: `@metin2gamebot`).
3. **Test Et** butonuna basın → gelen mesajın sahibi olduğunuz onaylanır → bağlantı hazır
   (yeşil "Bağlantı kuruldu" etiketi).

**Yapabildikleri:**
- Botu **Telegram'dan durdurma**: `Durdur` mesajı gönderildiğinde program Ctrl+O kısayolunu
  taklit ederek aktif modu durdurur ve *"Program Durduruluyor."* yanıtı verir.
- **Bildirim gönderme** (mesaja karakter ismi de eklenir):
  - Enerji modunda para/envanter uyarıları,
  - Metin2 simgesi tespit edilemediğinde yeniden giriş uyarısı,
  - Eşleşme/onay diyalogları.

**Token nasıl girilir?**
- **Derlenmiş exe:** `Metin2AutoFishCSharp.exe.config` içindeki `TelegramBotToken` anahtarı
  veya program içindeki *"Bot Token"* kutusu (bu durumda yanındaki `telegram.ini` dosyasına kaydedilir).
- **Kaynak kod:** `Sources/TelegramBot.cs` içindeki `telegramToken = "Add Your Token"`
  alanını kendi token'ınızla değiştirip derlemeniz gerekir.
  ⚠️ Token'ınızı **herkese açık repoya asla yazmayın**; `telegram.ini` de gizli tutulmalı.

### 4.6 Zamanlayıcı (Mola ve Durdurma)

`Sources/TimerGame.cs` — değerler **dakika** cinsindedir:

| Alan | Anlamı |
|---|---|
| **Min/Max Aktiflik** | Bot bu aralıkta rastgele süre boyunca çalışır |
| **Min-Max Mola** | Süre bitince bu aralıktan rastgele mola verir (örn. `3 6`) |
| **Oyun Durdurma** | Toplam süre sonunda oyunu kapatır ve botu tamamen durdurur (zamanlayıcı aktifken **zorunlu**) |

- Mola verdiğinde README'ye göre rastgele olarak **karakter atıp bekler veya çıkış atıp bekler**.
- Gerçekçilik için tüm beklemler `SleepRandom(min, max)` ile rastgeleleştirilir.

### 4.7 Ekran Görüntüsü Aracı (Screen Shot sekmesi)

Geliştirme/şablon üretimi için vardır:
- `Rectangle Info` alanına `x, y, genişlik, yükseklik[, saniye]` girilir (örn. `10,20,100,50`).
- **Take SShot**: geri sayımdan sonra bölgeyi yakalar, önizler ve seçilen klasöye
  (`Path Ways`: Desktop / Fishes / Images / Metin2 Alphabets / TestImages) PNG olarak kaydeder.
- Bu kaydedilen PNG'ler bir sonraki adımda **şablon (template)** olarak kullanılır.

### 4.8 Sürüm Kontrolü

`Sources/VersionChecker.cs`: Açılışta GitHub'daki `version.txt` dosyasını okur
(kaynak kodda URL sabit: `mmtcoder/.../version.txt`; `exe.config` içindeki
`UpdateCheckRepositoryOwner/Name` anahtarları hangi depoyu kontrol edeceğini belirler —
kendi fork'unuzu kullanacaksanız bu değerleri kendi hesabınıza çevirin).
Fark varsa *"Yeni sürüm mevcut"* diyalogu açar, istenirse güncelleme sayfasını tarayıcıda açar.

---

## 5. Kaynak Kod Haritası (Nerede Ne Var?)

```
mbot/
├── Metin2AutoFishCSharp.exe        → Derlenmiş program (çalışan dosya)
├── Metin2AutoFishCSharp.exe.config → Çalışma zamanı ayarları (token, sürüm deposu)
├── App.config                      → Kaynak tarafı config (derlemede exe.config olur)
├── version.txt                     → Sürüm numarası (1.0.2)
├── MusicPlayerApp.sln / .csproj    → Visual Studio çözüm/proje dosyaları
│
├── MainForm.cs (+Designer)         → Ana pencere: butonlar, sekmeler, olaylar
├── ChatHandlerForm.cs              → Chat kelime-cevap düzenleme penceresi
├── FullScreen.cs                   → Tam ekran şablon seçme penceresi
├── CheckGameCoordinate.cs          → Oyun koordinat sabitleri
├── GameObjectCoordinates.cs        → Tüm ekran bölgelerinin rect tanımları
│
├── Sources/
│   ├── Program.cs                  → Uygulama girişi (Main → MainForm)
│   ├── ScreenShotWinAPI.cs         → Ekran yakalama (GDI BitBlt)
│   ├── TelegramBot.cs              → Telegram bağlantısı, komutlar, bildirimler
│   ├── TimerGame.cs                → Rastgele zamanlayıcı / mola mantığı
│   ├── VersionChecker.cs           → GitHub sürüm kontrolü
│   ├── FileHandler.cs              → Klasör/yol/PNG kayıt yardımcıları
│   │
│   ├── Threads/                    → 3 thread mimarisi + global bayraklar
│   ├── GameHandler/                → Balık, Level, Enerji, oyun durumu kontrolleri
│   ├── LevelAndFarms/              → Statü, beceriler, otomatik av
│   ├── ChatHandler/                → OCR, chat/fısıltı, dosya kayıtları
│   ├── CharacterHandle/            → Karakter hareketi, envanter, özel pencereler
│   ├── ImageHandle/                → Şablon yükleme + görüntü işleme algoritmaları
│   ├── Inputs/                     → Mouse/klavye simülasyonu
│   ├── CoordinatesHandler/         → Rect/koordinat hesapları
│   └── Debugs/                     → Konsol/çizim hata ayıklama araçları
│
├── Fishes/                         → Balık/nesne şablon PNG'leri
├── Images/                         → Ekran durumu şablonları (giriş, ölüm, panel...)
├── ChatResources/
│   ├── GameAlphabets/              → OCR harf/rakam şablonları
│   └── ChatQuestionAnswer/         → Kelime-cevap kayıtları ve sohbet logları
├── ScreenShot/                     → Kaydedilen ekran görüntüleri
└── TestImages/                     → Test görselleri
```

---

## 6. Arayüzdeki Ayarların Karşılığı

| Arayüzdeki ayar | Kod karşılığı |
|---|---|
| Fishing Start / Level START / Enerji START | `ThreadGlobals.isFishingStopped` vb. + `ThreadsHandler.Start()` |
| HP/SP Yüzdesi (trackbar) | `ThreadGlobals.HP_SP_RATE[0..1]` |
| Statü Önceliği (HP/SP/STR/DEX) | `ThreadGlobals.STATUS_PRIORITY[0..3]` |
| Beceri süreleri (1-4, F1-F4) | `ThreadGlobals.SKILL_TIME_FOR_KEYS[0..7]` |
| Zamanlayıcı alanları | `TimerGame.MIN/MAX_WORK_TIME`, `MIN/MAX_BREAK_TIME`, `GAME_STOP_TIME` |
| Zamanlayıcı Aktif Et | `ThreadGlobals.isTimerBreakEnabled` |
| Adapte Tutma | `ThreadGlobals.isAdaptableFishing` |
| PC Yavaşsa | `TimerGame.IS_PC_SLOW` |
| Chat/Fısıltı Cevaplama | `isChattingAnswerActive` / `isWhisperAnswerActive` |
| Yerden ETP topla | `isETPPickUpActive` |
| Telegram Bot Aktif Et | `isTelegramBotActive` |
| Balık checkbox'ları | `isYabbieSelected`, `isPalamutSelected`, ... , `isHepsiSelected` |

---

## 7. Çalıştırma Gereksinimleri ve Kısıtlar

- **İşletim sistemi:** Windows 8 / 10 / 11, **.NET Framework 4.7.2** (güncel olmalı).
- **Oyun ayarı:** **800x600 pencere modu**, ekranın yeri **asla kıpırdamamalı**.
- Balıkçı NPC karakterin **arka tarafta görünür** olmalı; chat/isim kapatacak
  ekran öğeleri olmamalı.
- **Tek hesap** destekler; bot çalışırken bilgisayarda başka işlem yapılamaz
  (ekran ortak kullanıldığı için).
- Oyun penceresi önde ve görünür olmalı — arka plana alınırsa görüntü yakalanamaz.
- Hesap ölmüş/oyun kapanmışsa Thread 2 kendiliğinden yeniden girer
  (GameForge simgesi görev çubuğunda görünür olmalı, sadece bot hesabı açık olmalı).

## 8. Uyarılar

- ⚠️ **Macro kullanmak oyun kurallarına göre ban sebebidir.** Program eğitim amaçlıdır;
  sorumluluk kabul edilmez (bkz. README).
- ⚠️ **Telegram token'ı** ve `telegram.ini` gizli tutulmalı; token'lı config'i
  herkese açık paylaşılmamalı.
- ⚠️ Chat/fısıltı cevapları ve kayıtlar (`RecordChats.txt` vb.) kişisel veri içerir,
  paylaşımdan önce temizlenmeli.

## 9. Sık Karşılaşılan Durumlar

| Durum | Programın tepkisi |
|---|---|
| Karakter öldü | Ölüm ekranı tespit → çıkış → rastgele kanal değişimi ile devam |
| Oyun kapandı / atıldı | `metin2client` süreci yeniden oluşturulur, GameForge girişi denenir |
| Metin2 ikonu bulunamadı | Telegram'a uyarı gönderilir, yeniden giriş denenir |
| Satım (sale) penceresi açıldı | Otomatik kapatılır (v2.2 düzeltmesi) |
| Ticaret paneli açıldı | Kapatılır ve chat'e uyarı gönderilir |
| Yakında oyuncu var (Adapte açık) | Daha yavaş balık tutulur |
| Süre doldu (zamanlayıcı) | Oyun ayarlardan kapatılır, bot durur |
| `Ctrl+O` / Telegram "Durdur" | Aktif mod durdurulur |
