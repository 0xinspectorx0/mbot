# Hazırlık Modülü — Solucan, Kamp Ateşi ve Izgara (PrepareFishing.cs)

> Bu belge, balık botunun **hazırlık aşamasını** yürüten `Sources/GameHandler/PrepareFishing.cs`
> dosyasını (921 satır) fonksiyon fonksiyon, adım adım açıklar.
> İlgili belge: [REHBER.md](REHBER.md) §4.1

---

## 1. Genel Bakış

**Sınıf:** `MusicPlayerApp.Sources.GameHandler.PrepareFishing` (internal)

Balık tutmaya başlamadan önce karakteri hazırlar: balıkçıyı bulur, **Kamp Ateşi** alıp
yere serer, envanterdeki balıkları **ızgarada yakar** (envanter yeri açar), **solucan** alır
200'lük yığınlar haline getirir ve **hızlı erişim slotlarına** dizer, sonra karakteri
**balık noktasına** yürütür.

**Bağımlılıklar (constructor'da oluşur):**

| Alan | Sınıf | Görevi |
|---|---|---|
| `imageObjects` | `ImageObjects` | Şablon yükleme + karşılaştırma |
| `inputGame` | `GameInputHandler` | Sahte mouse/klavye girdisi |
| `charThings` | `CharSpecialThings` | Envanter/pencere işlemleri (miras: `CharInfo`) |
| `coordinate` | `GameObjectCoordinates` | Tüm ekran rect'leri |
| `screenShot` | `ScreenShotWinAPI` | Ekran görüntüsü alma |
| `listWorm200` | `List<Rectangle>` | Tespit edilen 200'lük solucan yığınları |

**Kendi alanları (state):**

| Alan | Tip | Açıklama |
|---|---|---|
| `xGrillFishes`, `yGrillFishes` | `int` | "Hepsi" ızgara modunda grid konumu (5 kolon × 9 satır) |
| `pageGrillFisher` | `int` | "Hepsi" modunda envanter sayfası (1 veya 2), başlangıç **1** |
| `NEEDED_WORM200_COUNT` | `const int = 32` | Hedef: **32 adet 200'lük solucan yığını** (32 × 200 = 6400 solucan) |
| `isGrillFishesFailed` | `bool` | Izgara zaman aşımında true → grid sayaçları sıfırlanır |
| `listWorm200` | `List<Rectangle>` | Birleştirme sırasında kullanılan geçici liste |

**Dış etkilediği bayrak:** `ThreadGlobals.isPrepareFishingStarted`
(hazırlık sürerken Thread 3 chat/fısıltı işlerini atlar — `ThreadsHandler` içinde kontrol edilir).

---

## 2. ⚠️ Önce Zamanlayıcı Semantiği (çok önemli)

Bu modüldeki tüm `CheckDelayTimeInSecond(n)` çağrılarının anlamı **tersine yakındır**:

```csharp
// TimerGame.cs:145
public bool CheckDelayTimeInSecond(long delayTime)
{
    // Geçen süre < delayTime ise TRUE döner → "hâlâ süre var" demektir!
    return (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - StartedSecondTime) < delayTime;
}
```

- **`true`** → süre henüz dolmadı (zaman penceresi içindesin, işleme devam)
- **`false`** → süre doldu (zaman aşımı — genelde `return false/Empty` ile çıkılır)

Yani `if (timer.CheckDelayTimeInSecond(40)) { normal yol } else { zaman aşımı yolu }`
şeklinde okunmalıdır. Ayrıca timer, ilk çağrıda kendiliğinden başlar
(`SetStartedSecondTime()` çağrılmamışsa) ve **kod içinde çoğu yerde resetlenmez**.

---

## 3. Ana Akış — Adım Adım

`StartPrepareFishing()` üç aşamayı **sırayla** çalıştırır:

```
StartPrepareFishing()
│
├─ 0) isPrepareFishingStarted = true   ← Thread 3'ü kilitle
│
├─ AŞAMA 1 ─ FindFisher()              ← Balıkçıyı bul, dialogu aç, mağazayı aç
│
├─ AŞAMA 2 ─ GrillFishingHandle()      ← Kamp Ateşi al-yerleştir, balıkları ızgarada yak
│   ├─ BuyKampAtasiFromFisher()          (mağazadan kamp ateşi satın al)
│   ├─ GetFishTypesForGrilling()         (hangi balıklar ızgaralanacak)
│   ├─ FireKampAtesi()                   (ateşi yere ser, yeşil başlığını bul)
│   └─ GrillFishes()                     (balıkları ateşe sürükle → yak)
│
├─ AŞAMA 3 ─ WormsHandle()             ← Solucan al/birleştir/slotlara diz
│   ├─ CombineAndCountWorms()            (200'lük yığınları birleştir, 32'ye tamamla)
│   │   └─ BuyFiftyWormAsNeeded()        (eksik kadar 50'lik satın al)
│   ├─ CloseFisherShopPage()
│   ├─ InsertObjectToSkillSlots(...)     (32 stack → 4 shift sayfası × 8 slot)
│   └─ GoToFishPlace()                   (balık noktasına yürü)
│
└─ isPrepareFishingStarted = false    ← kilidi aç
```

**Başlangıç sırası önemli:** modül `CheckFisherShopPage()` (mağaza açık mı) ile çalışır;
mağaza kapalıysa kendi içinde `CheckFisherIsThere()` ile yeniden açmayı dener.

---

## 4. Aşama 1 — Balıkçıyı Bulma

### 4.1 `FindFisher()` — private void

**Görev:** Karakteri kuşbakışı kamera ile sağarak-balıkçıyı arar; bulduğunda dialogu ve
mağaza sayfasını açar (asıl tıklama/bağlama `CheckFisherIsThere` içindedir).

**Adım adım:**

1. `SleepRandom(1500, 2000)` — insansı başlangıç gecikmesi.
2. Değişkenler: `zigZagWalking = 0`, `walkSide = 1` (sağ/sol alternasyonu).
3. Panelleri kapatır: `OpenCloseSettingButton(false)`, `OpenCloseInventory(false)`.
4. `BirdViewPerpective()` — **F + G tuşlarını ~1,4 sn basılı tutar** (kamerayı kuşbakışa alır;
   `CharInfo.cs:103`'te tanımlı).
5. Zamanlayıcıyı başlatır.
6. **`while (!CheckFisherIsThere())`** döngüsü:
   - **Çıkış bayrakları:** `isFishingStopped || isCharKilled` → `return` (bot durdurulmuş/ölüm).
   - **Süre henüz dolmadıysa (< 40 sn) — zikzak yürüyüş:**
     1. `FindBorderAreaBetweenColors(RectWoodDetectinonArea, MIN_WOOD_VALUE, MAX_WOOD_VALUE)`
        ile ekranın **alt şeridinde** (22, 434, 800×2 — karakterin önü/zemini) ahşap zemin
        sınırı bulunur.
     2. Bulunursa: `zigZagWalking = random(0,120)`; `walkSide` 1 ise merkezin **sağına**,
        2 ise **soluna** ofsetle `MouseMoveAndPressLeft` ile tıklanır (karakter zikzak yürür).
     3. Bulunamazsa: şeridin ortasına tıklanır (kör tahmin).
   - **Süre dolduysa (≥ 40 sn) — yedek hareket:**
     1. `SettingButtonClick(CHAR_BUTTON)` (menüden karakter butonu).
     2. `isSettingButtonSeemed` olana kadar döngüyle bekle (bot durdurulursa `return`).
     3. Panelleri tekrar kapat, `SleepRandom(4000, 5500)` bekle.
     4. **A + S** tuşlarını 1–1,5 sn basılı tut → bırak (sol-geri çapraz hareket).
     5. **S + D** tuşlarını 1–1,5 sn basılı tut → bırak (geri-sağ çapraz hareket).
     6. Zamanlayıcıyı sıfırla (yeni 40 sn penceresi).
     > Amaç: sıkışmış karakteri kurtarıp kamerayı farklı yöne çevirerek balıkçıyı görüş
     > alanına yeniden sokmak.

**Başarı:** döngü ancak `CheckFisherIsThere()` true dönerse biter (mağaza açılmıştır).

---

### 4.2 `CheckFisherIsThere()` — private bool

**Görev:** Ekranın tamamında yeşil **"Balıkçı"** NPC başlığını (mini harita/oyun içi yeşil
başlık) arar; bulunca tıklar, açılan NPC dialogundan mağaza seçeneğini seçer, mağaza
sayfası açılana kadar bekler.

**Adım adım:**

1. `Stopwatch` başlatılır (performans logu).
2. **Hedef maske:** `RecordWantedColorAsBool(MAP_BALIKCI_GREEN, arrayFisherWords)` —
   kayıtlı "Balıkçı" kelime örneğinden yalnızca `0xFF7AE75D` yeşil piksellerinin bool maskesi.
3. **Tarama maskesi:** `ImageArraySpecifiedArea(RectMetin2GameScreen())` — oyun ekranının
   tamamı için aynı renk filtresi.
4. **Çift döngü (x, y):** `IsMatchBoolArrays(hedef, RectFisherSample(27×7), tarama, ekran, x, y)`
   — piksel piksel tam şablon eşleşmesi aranır (tam ekran taraması, maliyetli).
5. **Eşleşme bulunursa:**
   1. Tıklama noktası: `x + RectMetin2GameScreen.X + (27/2) + currentScreenGamePoint.X`,
      `y + ... + (7/2) + currentScreenGamePoint.Y`
      (`currentScreenGamePoint` = Metin2 penceresinin masaüstündeki konumu — `CheckGameCoordinate.cs`).
   2. Balıkçıya sol tık → `SleepRandom(700, 1000)` (dialog açılsın).
   3. `RectFisherOptionsPage` (388, 244, 20×11) bölgesinin beyaz pikselleri
      (`CHAT_WHITE_COLOR`) `arrayBalikciAraEkran` şablonuyla `SENSIBILTY_HIGH` karşılaştırılır:
      - **Eşleşti (dialog geldi):**
        1. Seçeneğin sol-üst köşesine tıkla (mağaza seçeneği).
        2. `SleepRandom(1400, 1660)` bekle.
        3. **`while (!CheckFisherShopPage())`**: tekrar tıkla + bekle;
           `isFishingStopped || isCharKilled` ise `return false`.
        4. `return true`.
      - **Eşleşmedi (dialog yok / yanlış yerde):**
        1. **W** tuşu 300–500 ms basılı tut (karakteri ileri yürüt — yaklaşır).
        2. `return CheckFisherIsThere()` — **özyineleme (recursion)** ile taramayı tekrarla.
6. Hiç eşleşme yoksa: süre logu, `return false`.

**Not:** Bu fonksiyon içinde bayrak kontrolü yalnızca tıklama sonrası bekleme döngülerinde
vardır; tam ekran tarama bölümünde yoktur.

---

## 5. Aşama 2 — Kamp Ateşi ve Izgara

### 5.1 `GrillFishingHandle()` — private void

**Görev:** Izgara işleminin üst düzey orkestratörü. Mağaza açıkken kamp ateşi alır ve
balıkları yakar; değilse balıkçıyı bulup yeniden dener.

**Adım adım:**

1. **`if (CheckFisherShopPage())`** — mağaza açık mı?
   - **Evet:**
     1. `BuyKampAtasiFromFisher()` — kamp ateşi yoksa satın al.
     2. **`if (!ThreadGlobals.isHepsiSelected)` — seçili balık modu:**
        1. `fishResult = GetFishTypesForGrilling()` → 5 elemanlı dizi
           (seçili olmayanların index'i `null`).
        2. `null olmayan` elemanları say (`fishTypes`).
        3. `fishTypes > 0` ise:
           - Her balık tipi için **sayfa 1** ve **sayfa 2** envanter taraması:
             `CheckObjectInventory(fishTipi, RectItemSlotSizeSample(32×16), Page_1/Page_2)`
             → `fishCoordinatesPageOne[]` / `fishCoordinatesPageTwo[]` rect dizileri.
           - **Erken çıkış:** `pageOne[0] && [1] && [2]` hepsi boşsa → `return`
             (yabbie/altın sudak/palamut yoksa ızgaralamayı hiç denemez —
             §11'deki "bilinen tuhaflık"a bakın).
           - `FireKampAtesi()` → yere serilmiş ateşin yeşil başlık rect'i
             (`rectKampGreenResult`, `Empty` değilse devam).
           - **Yeniden deneme döngüsü:** `while (!GrillFishes(one, two, green))`
             içinde: `CheckFisherIsThere()` → `CheckFisherShopPage()` →
             `BuyKampAtasiFromFisher()` → `FireKampAtesi()` (ateş sönmüşse/bittiyse yenile)
             → tekrar `GrillFishes`.
        4. `fishTypes == 0` → hiçbir şey yapmaz.
     3. **`else` — "Hepsi" modu:**
        1. `FireKampAtesi()` → green rect.
        2. `while (!GrillFishes(null, null, green))` — aynı yenileme döngüsü
           (seçili balık aramaz, envanterdeki her dolu slotu atar).
   - **Hayır (mağaza kapalı):** `if (CheckFisherIsThere())` →
     **`GrillFishingHandle()` özyinelemesi** (mağazayı açıp baştan).

---

### 5.2 `GetFishTypesForGrilling()` — private int[][]

**Görev:** Izgaraya atılacak balık tiplerinin şablon dizilerini, sabit 5 indexli diziyle döndürür.

| Index | Bayrak | Şablon |
|---|---|---|
| `[0]` | `isYabbieSelected` | `arrayYabbieIcon` |
| `[1]` | `isAltinSudakSelected` | `arrayAltinSudakIcon` |
| `[2]` | `isPalamutSelected` | `arrayPalamutIcon` |
| `[3]` | `isKurbagaSelected` | `arrayKurbagaIcon` |
| `[4]` | `isKadifeSelected` | `arrayKadifeIcon` |

Seçili olmayan tipler için index `null` kalır. **Dikkat:** `isDenizkizSelected`
(denizkızı) bu listede **YOK** — denizkızı anahtarı ızgaraya atılmaz (yalnızca tutma modunda).

---

### 5.3 `BuyKampAtasiFromFisher()` — private void

**Görev:** Balıkçı mağazasından Kamp Ateşi satın al (envantere düşünceye kadar).

**Adım adım:**

1. **`if (CheckFisherShopPage())`:**
   - **`while` (Page 1'de kamp ikonu YOK && Page 2'de kamp ikonu YOK):**
     - `CheckObjectInventory(arrayKampIcon, RectItemSlotSizeSample, Page_1/2)` kullanılır.
     - **`if (timer.CheckDelayTimeInSecond(6))` — 6 sn pencere içinde:**
       - Çıkış (bilinçli, §11'e bakın): `if (isFishingStopped && isCharKilled) return;` (**AND** —
         kod olduğu gibi böyle, normalde OR olurdu).
       - `MouseMoveAndPressRight(PointFisherShopKampAtesi)` — mağaza satırına
         **sağ tık** (satın al) → `SleepRandom(500, 800)`.
     - **Pencere doldu:** log `"kamp atesi alinamadi"` → `return`.
   - Döngü biterse (ikon envanterde) iş biter.
2. **`else` (mağaza kapalı):** `CheckFisherIsThere()` → sonuç yok sayılır →
   **`BuyKampAtasiFromFisher()` özyinelemesi**.

---

### 5.4 `FireKampAtesi()` — private Rectangle

**Görev:** Kamp Ateşi'ni envanterden **yere serer** ve haritadaki yeşil
**"Kamp Ateşi"** başlığının rect'ini döndürür (ızgarada tıklanacak hedef nokta).

**Adım adım:**

1. `CloseFisherShopPage()` — önce mağazayı kapat (ateş yere serilirken pencereler kapalı olsun).
2. Kamp ikonunu bul: önce Page 1, yoksa Page 2 (`CheckObjectInventory(arrayKampIcon, ...)`).
3. **İkon varsa:**
   1. Hedef maske: `RecordWantedColorAsBool(MAP_CAMP_FIRE_GREEN, arrayKampAtesiWords)` —
      yeşil `0xFF7AE75D` (balıkçı başlığıyla aynı yeşil).
   2. **`while (FindAllImagesBoolArrays(mask, RectKampGreenSample(48×7),
      RectKampAtesiAsagiTarafKoordinat(380, 330, 64×60), renk).Length <= 0)`**
      — oyun ekranının alt-ortasında yeşil başlık görünene kadar:
      - **`if (timer.CheckDelayTimeInSecond(20))` — 20 sn pencere içinde:**
        - Bayrak çıkışı: `isFishingStopped || isCharKilled` → `Rectangle.Empty`.
        - **S** tuşu 400–500 ms bas (karakter bir adım geri atsın — ateş önüne serilsin).
        - Kamp ikonunun **üstüne sağ tık** (`MouseMoveAndPressRight`, merkez X, üst Y) →
          oyun ateşi yere bırakır.
        - `SleepRandom(300, 400)` → başlık aramaya devam.
      - **Süre doldu:** log → `Rectangle.Empty`.
   3. Başlık bulunduysa: **`return` ilk eşleşen rect** (ateşin ekrandaki yeşil başlığı —
      `GrillFishes` bunun merkezine tıklayarak balığı ateşe "kullanır").
4. **İkon yoksa:** log `"kamp ateşi bulunamadi"` → `Rectangle.Empty`.

---

### 5.5 `GrillFishes(Rectangle[][] rectPageOne, Rectangle[][] rectPageTwo, Rectangle kampAtesiGreen)` — private bool

**Görev:** Envanterdeki balıkları tek tek kamp ateşine sürükleyip (iki tıklama ile)
yakar. İki modu vardır. **True** = iş bitti; **false** = timeout/diyalog/hata → üst döngü
yeniler.

> **Ortak mekanizma (her iki modda):** "kullan" = balık slotunun **üst ortasına sol tık**
> (eşyayı al) → `kampAtesiGreen` merkezine **sol tık** (ateşe bırak/kullan).
> Ardından `RectYereAtmaAlgilama` (448, 289, 48×16) içinde **"yere atma" dialog'u**
> (`arrayYereAtmaDialog`, HIGH) kontrol edilir — çıkarsa item ateşe gitmiyor demektir:
> **ESC** basılır ve `return false`.
> Slot görseli (`slotImageAfterGrill`) her turda yeniden okunur; artık şablonla eşleşmiyorsa
> balık yanmış/ gitmiş demektir → sıradaki slota geçilir.

#### Mod A — Seçili balıklar (`!isHepsiSelected`)

1. **Sayfa 1:**
   - `ClickWantedInventoryPage(Page_1)`.
   - Dizilerdeki her `rectFish` için:
     - `fishImageBeforeGrill` ve `slotImageAfterGrill` **aynı anda** okunur
       (ilk karşılaştırma kasıtlı olarak eşittir → döngüye girilir).
     - **`while (slot hâlâ balık):`**
       - `if (timerGrillFishes.CheckDelayTimeInSecond(60))` — **ortak 60 sn pencere**:
         - `isFishingStopped || isCharKilled` → `return false`.
         - Balığın üst ortasına tık → kamp ateşi merkezine tık.
         - `SleepRandom(300, 400)`.
         - Yere atma dialog'u varsa → **ESC** + `return false`.
         - `slotImageAfterGrill` yeniden okunur (değiştiyse döngü biter).
       - **Süre doldu:** log → `return false`.
2. **Sayfa 2:** aynı mantık (bekleme `SleepRandom(200, 400)`).
3. Hata yoksa bloğun sonuna kadar gelinir → §5.5'in sonundaki **sayaç sıfırlama + `return true`**.

#### Mod B — "Hepsi" (`isHepsiSelected`)

`pageGrillFisher` (1→2), `y` (0→8), `x` (0→4) iç içe döngüleri — yani **5 kolon × 9 satır =
45 slot/sayfa, toplam 90 slot** (`RectFirstSlotPlace` = (640, 286, 32×16),
`DISTANCE_BTWN_INV_SLOTS = 32`):

1. `isGrillFishesFailed` true ise sayaçlar sıfırlanır (`page=1, x=0, y=0`).
2. Sayfa 1 mi 2 mi → `ClickWantedInventoryPage`.
3. Her slot için:
   - Bayrak çıkışı → `return false`.
   - `if (timer.CheckDelayTimeInSecond(60))` — süre dolmamış:
     - Slot görseli okunur; `arrayEmptySlotPlace` ile HIGH karşılaştırma:
       - **Boş slot** → atla (hiçbir şey yapma).
       - **Dolu slot** → slotun üst ortasına tık → kamp ateşi merkezine tık →
         `SleepRandom(200, 400)` → yere atma dialog'u? → **ESC** + `return false`.
   - **Süre doldu:** `isGrillFishesFailed = true; return false`
     (üst döngü yenilerken sayaçlar bu bayrak sayesinde kaldığı yerden/devam modunda başlar).
4. İki sayfa da biterse: `isGrillFishesFailed = false; page=1; x=0; y=0;` → **`return true`**.

> README'deki not: "envanterdeki bütün objeleri ateşe sürükler, sadece balıklar yanar" —
> oyun tarafında yanmayan eşya için yere atma dialog'u çıkar ve kod ESC ile bırakır.

---

## 6. Aşama 3 — Solucan

### 6.1 `WormsHandle()` — private void

**Görev:** Mağazada solucanları tamamla, birleştir, hızlı erişim slotlarına diz, balık
noktasına git.

**Adım adım:**

1. **`if (CheckFisherShopPage())`:**
   1. **`if (CombineAndCountWorms())`** — 32 yığın hazır mı?
      - **Evet:**
        1. `CloseFisherShopPage()`.
        2. `InsertObjectToSkillSlots(arrayWorm200, RectItemSlotSizeSample(32×16), INSERT_COUNT_32)`
           (`CharSpecialThings.cs:487`): iki sayfalık envanterdeki 200'lük yığınları bulur,
           **4 shift sayfası × 8 slot = 32 hızlı erişim slotuna** dağıtır (90 sn bütçe,
           `CheckShiftPageNumber` ile Shift+1..4 sayfaları).
           - **Başarılı:** `GoToFishPlace()` → `listWorm200.Clear()` → `OpenCloseInventory(false)`.
           - **Başarısız:** `listWorm200.Clear()` → envanteri kapat (slotlar doldu/oyun durdu).
      - **Hayır:** yalnızca log (`"CombineAndCountWorms returned false ..."`).
2. **`else` (mağaza kapalı):** `if (CheckFisherIsThere())` →
   **`WormsHandle()` özyinelemesi** (mağazayı açıp tekrar dene).

---

### 6.2 `CombineAndCountWorms()` — private bool

**Görev:** Envanterdeki solucanları **200'lük yığınlara birleştir**, adet 32'ye ulaşana
kadar satın alma döngüsünü yönet. Hedefe ulaşırsa `true`, zaman aşımına/hataya `false`.

**Adım adım:**

1. `listWorm200.Clear()`; `worms200CharHave = CombineItemsTo200(arrayWorm200)`
   (`CharSpecialThings.cs:721` — envanterdeki dağınık solucanları birleştirip
   200'lük yığın **sayısını** döndürür; `−1` = hata).
2. **`while (worms200CharHave < 32)`:**
   - **`if (timerCombine.CheckDelayTimeInSecond(500))` — 500 sn bütçe içinde:**
     - `worms200CharHave != -1` ise:
       1. Bayrak çıkışı: `isFishingStopped || isCharKilled` → `false`.
       2. `BuyFiftyWormAsNeeded(worms200CharHave)` — eksik kadar satın al.
       3. `listWorm200.Clear()` → yeniden birleştir/say.
       4. **`HoverAllInventorySlotsWithoutClick()`** — satın alma sonrası 1. ve 2.
          envanter sayfasındaki **tüm 90 yuvaların üzerinden tıklamadan** imlec gezdirilir
          (bkz. §6.4), **sonra** `CombineItemsTo200` ile birleştirmeye devam edilir.
     - `worms200CharHave == -1` ise: sadece yeniden dene (birleştirme hatası).
   - **Süre doldu:** log → `return false`.
3. `worms200CharHave >= 32` → **`return true`**.

---

### 6.3 `BuyFiftyWormAsNeeded(int wormsCharHave)` — private void

**Görev:** Balıkçı mağazasındaki **50'lik solucan** satırına arka arkaya sağ tıklayarak
eksik yığınları satın al.

**Adım adım:**

1. **`if (CheckFisherShopPage())`:**
   1. `neededWorms = 32 − wormsCharHave` (eksik yığın sayısı).
   2. **`for (i = 0; i < neededWorms * 4; i++)`** — her sağ tık 50 adet alır;
      **4 tık = 200 = 1 yığın** (`neededWorms × 4` tık):
      1. `MouseMoveAndPressRight(PointFisherShopFiftyWorm)` — (501, 116) satırına sağ tık.
      2. `SleepRandom(500, 600)`.
      3. **Envanter dolu kontrolü:** `arrayFullDialogInFisherShop` şablonu
         `RectYereAtmaAlgilama` içinde HIGH ile aranır:
         - **Eşleşti ("yer yok" dialog'u):** log → **ESC** → `return`
           (daha fazla satın alma yapılmaz; üst fonksiyon devam eder/biter).
2. **`else`:** log `"Fisher shop page is not open"` → `return`.

---

### 6.4 `HoverAllInventorySlotsWithoutClick()` — private void

**Görev:** Yem satın alma işleminden sonra, birleştirme işlemine devam etmeden önce
**1. ve 2. envanter sayfasındaki tüm yuvaların üzerinden sırayla imleci gezdirmek —
hiç tıklama yapmadan.** (Kullanıcı isteğiyle eklendi.)

**Adım adım:**

1. Bayrak çıkışı: `isFishingStopped || isCharKilled` → `return`.
2. `OpenCloseInventory(true)` — envanter açık olmalı (yuvalar görünür olsun).
3. **`for page = 1..2`:**
   1. `ClickWantedInventoryPage(Page_1 / Page_2)` — sayfa sekmesine tıklanır
      (sayfa değiştirmenin başka yolu yok; **yuvaların üzerine tıklanmaz**).
   2. **`for y = 0..8`, `for x = 0..4`** (satır içi, soldan sağa — 5×9 = 45 yuva/sayfa):
      1. Bayrak kontrolü → çıkış.
      2. Yuva merkezi hesaplanır:
         `X = RectFirstSlotPlace.X + 32*x + width/2`,
         `Y = RectFirstSlotPlace.Y + 32*y + height/2`
         (`DISTANCE_BTWN_INV_SLOTS = 32`).
      3. **`inputGame.MouseMove(x, y)`** — yalnızca `SetCursorPos` (imlec taşıma,
         **tıklama yok**); `MouseMove` içinde zaten 40–60 ms rastgele bekleme var.
4. Toplam: 2 sayfa × 45 yuva = **90 hover**, ~4–6 saniye sürer; bitişte log basılır.
5. Dönüşten sonra akış `CombineItemsTo200` ile **birleştirmeye devam eder**
   (çağrı yeri: `CombineAndCountWorms`, §6.2 adım 4).

---

## 7. Aşama 4 — Balık Noktasına Gitme

### 7.1 `GoToFishPlace()` — public void

**Görev:** Karakteri **balıkçının tersi yöne**, zikzak yaparak yürütür (balık noktasına
gidiş). `WormsHandle` sonunda ve "balık tutamazsın" pembe mesajlarında ayrıca çağrılır
(`FishingHandle.cs:621,630`).

**Adım adım:**

1. **10 deneme** (`for k = 0..9`):
   - `isFishingStopped` → `return`.
   - `FindBorderAreaBetweenColors(RectReverseWoodDetectionArea, MIN_WOOD_VALUE, MAX_WOOD_VALUE)`:
     bu sefer ekranın **üst şeridi** (70, 187, 800×2) taranır — üst bölge karakterin
     **arkası/sahnenin gerisi** olduğu için ters yöne tıklama = geriye yürümek.
   - Bulunursa: `zigZag = random(0,120)`, `walkSide` ile sağ/sol alternasyonlu tıklama.
   - Bulunamazsa: şeridin ortasına tıklama.
   - `SleepRandom(50, 100)` — çok kısa aralıklarla (10 hızlı tıklama).
2. Döngü biter (fonksiyon "gitmeye devam et" sinyali vermez; ana balık döngüsü kendi
   tıklamalarıyla ilerletir).

---

## 8. Ortak Fonksiyonlar

### 8.1 `CheckFisherShopPage()` — public bool

**Görev:** Balıkçı mağazası açık mı?

- `CompareTwoArrayAdvanced(arrayFisherShopPage, ImageArraySpecifiedArea(
  RectFisherShopPage), SENSIBILTY_HIGH)` → `true/false`.
- `RectFisherShopPage` = (482, 58, 22×6) — mağaza başlığının köşesi (pencere konumu + `CheckGameScreenPlace` ofsetli).

### 8.2 `CloseFisherShopPage()` — public void

**Görev:** Mağaza sayfasını kapatana kadar kapatma düğmesine basar.

1. **`while (CheckFisherShopPage())`:**
   - **`if (timer.CheckDelayTimeInSecond(20))` — 20 sn pencere:**
     - Çıkış: `isFishingStopped || isCharKilled || !isSettingButtonSeemed` → `return`.
     - `MouseMoveAndPressLeft(PointFisherShopCloseButton)` — (578, 53) kapatma düğmesi.
     - `SleepRandom(400, 600)`.
   - **Süre doldu:** log → `return`.

### 8.3 Yapıcılar (constructors)

| İmza | Kullanan | Fark |
|---|---|---|
| `PrepareFishing(ImageObjects)` | `FishingHandle.cs:82` (`prepareFish`) | Kendi `CharSpecialThings`'ini oluşturur |
| `PrepareFishing(ImageObjects, CharSpecialThings)` | `CharSpecialThings.cs:68` (kendi içinde) | Dışarıdan paylaşılan `charThings` kullanılır (shift sayfası gibi static durum ortak kalır) |

---

## 9. Yardımcı Fonksiyon Sözlüğü (diğer sınıflardan)

### `CharSpecialThings` / `CharInfo`

| Fonksiyon | Ne yapar |
|---|---|
| `OpenCloseSettingButton(bool state)` | Ayarlar/karakter panelini açık/kapalı hale getirir (`isSettingButtonSeemed` üzerinden) |
| `OpenCloseInventory(bool state)` | Envanter penceresini açar/kapatır |
| `ClickWantedInventoryPage(InventoryPage)` | Envanterin Sayfa 1 / Sayfa 2 sekmesine tıklar |
| `CheckObjectInventory(int[], Rectangle, InventoryPage)` | Verilen şablonu (`FindAllImagesOnScreen`) o sayfanın `RectInventoryPageArea` içinde arar → `Rectangle[]` |
| `CheckObjectTwoPageInventory(int[], Rectangle)` | İki sayfayı da tarar; sona `PageOneRectangle()` işaretçisini de ekler |
| `CombineItemsTo200(int[])` | Solucanları 200'lük yığınlara birleştirir, yığın **sayısını** döndürür (−1 hata); sayfa grid'ini (`pageCombineItems200`, `x/y`) static durumla sürdürür |
| `InsertObjectToSkillSlots(int[], Rectangle, InsertCountSetting)` | Yığınları 4 shift sayfası × 8 slot = 32 hızlı erişim slotuna dizer (`INSERT_COUNT_32` / `INSERT_COUNT_1`), 90 sn bütçe |
| `SettingButtonClick(SettingButtonPrefers)` | Menüde CHAR/EXIT gibi butonlara tıklar |
| `BirdViewPerpective()` (CharInfo) | **F+G** ~1,4 sn basılı tutarak kamerayı kuşbakışa alır |

### `ImageObjects` / `ImageProcess`

| Fonksiyon | Ne yapar |
|---|---|
| `RecordWantedColorAsBool(color, şablon)` | Şablondaki istenen rengin bool maskesini üretir (balıkçı/kamp yeşili için) |
| `RecordWantedColorIntArray(color, bölge)` | Ekran bölgesindeki istenen rengin int dizisini üretir (beyaz dialog yazısı için) |
| `IsMatchBoolArrays(hedef, hedefRect, tarama, taramaRect, x, y)` | (x,y)'den itibaren tam şablon eşleşmesi |
| `FindAllImagesBoolArrays(mask, sample, bölge, renk)` | Bölgede maskeye uyan tüm rect'leri bulur (yeşil kamp başlığı) |
| `FindAllImagesOnScreen(şablon, size, bölge)` | Normal şablon taraması (envanterdeki ikonlar) |
| `CompareTwoArrayAdvanced(a, b, sensibility)` | İki piksel dizisini duyarlılık seviyesiyle karşılaştırır |
| `FindBorderAreaBetweenColors(rect, min, max)` | Rect içinde min–max arası renk sınırının bulunduğu alanı döndürür (ahşap zemin → tıklama hedefi) |
| `FindBorderAreaForWantedColor(...)` | Tek renk sınırı arar |

### `GameInputHandler` / `KeyboardInput`

| Fonksiyon | Ne yapar |
|---|---|
| `MouseMoveAndPressLeft(x, y)` | İmleci taşı + sol tık |
| `MouseMoveAndPressRight(x, y)` | İmleci taşı + sağ tık (satın alma, yerleştirme) |
| `KeyDown / KeyRelease(ScanCodeShort)` | Scan code ile tuş bas/bırak (A, S, D, W, F, G, ESC) |
| `KeyPress(ScanCodeShort)` | Tuşa bas-bırak (ESC) |

### `TimerGame`

| Fonksiyon | Ne yapar |
|---|---|
| `SetStartedSecondTime()` | Zamanlayıcıyı sıfırla/başlat |
| `CheckDelayTimeInSecond(n)` | **`true` = hâlâ n saniye dolmadı** (bkz. §2) |
| `SleepRandom(min, max)` | Rastgele `Thread.Sleep` (insansı gecikme) |
| `MakeRandomValue(min, max)` | Rastgele tam sayı (0–120 zikzak ofseti) |

---

## 10. Sabitler, Renkler ve Koordinatlar

### Renkler (`ColorGame.cs`)

| Sabit | Değer | Kullanım |
|---|---|---|
| `MAP_BALIKCI_GREEN` | `0xFF7AE75D` | Balıkçı başlığı tespiti |
| `MAP_CAMP_FIRE_GREEN` | `0xFF7AE75D` | Kamp Ateşi başlığı (aynı yeşil) |
| `MIN_WOOD_VALUE` / `MAX_WOOD_VALUE` | `0xff191108` – `0xffa58b75` | Ahşap zemin yürüme alanı |
| `CHAT_WHITE_COLOR` | `0xFFFFFFFF` | NPC dialog seçeneği / mağaza yazıları |
| `CHAT_PINK_COLOR` | `0xffffc8c8` | "Yer yok", "balık tutamazsın" mesajları (tetikleyici) |

### Koordinatlar (`GameObjectCoordinates.cs` — hepsi `CheckGameScreenPlace()` ofsetli)

| Fonksiyon | Değer (x, y, w×h) | Anlam |
|---|---|---|
| `RectWoodDetectinonArea` | 22, 434, 800×2 | Alt şerit → **ileri** yürüme zemini |
| `RectReverseWoodDetectionArea` | 70, 187, 800×2 | Üst şerit → **geri** yürüme zemini |
| `RectMetin2GameScreen` | (tam oyun alanı) | Balıkçı başlığı taraması |
| `RectFisherSample` | 0, 0, 27×7 | "Balıkçı" başlık şablon boyutu |
| `RectFisherOptionsPage` | 388, 244, 20×11 | NPC dialog seçeneği |
| `RectFisherShopPage` | 482, 58, 22×6 | Mağaza açık mı göstergesi |
| `PointFisherShopFiftyWorm` | 501, 116 | Mağazada 50'lik solucan satırı |
| `PointFisherShopKampAtesi` | 469, 85 | Mağazada Kamp Ateşi satırı |
| `PointFisherShopCloseButton` | 578, 53 | Mağaza kapatma düğmesi |
| `RectYereAtmaAlgilama` | 448, 289, 48×16 | "Yere atılsın mı?" dialog bölgesi |
| `RectKampAtesiAsagiTarafKoordinat` | 380, 330, 64×60 | Yere serilen ateşin yeşil başlık bölgesi |
| `RectKampGreenSample` | 0, 0, 48×7 | Kamp başlık şablon boyutu |
| `RectFirstSlotPlace` | 640, 286, 32×16 | Envanter ilk slot (grid başlangıcı) |
| `RectItemSlotSizeSample` | 0, 0, 32×16 | Slot şablon boyutu |
| `DISTANCE_BTWN_INV_SLOTS` | 32 px | Slot aralığı (5 kolon × 9 satır) |

### Zamanlayıcı / Bütçe Tablosu

| Fonksiyon | Bütçe | Ne zaman sıfırlanır |
|---|---|---|
| `FindFisher` | **40 sn** (zikzak → yedek hareket) | Yedek hareket sonunda |
| `CombineAndCountWorms` | **500 sn** (satın al+birleştir döngüsü) | Fonksiyon başında |
| `BuyKampAtasiFromFisher` | **6 sn** | Tek çağrı |
| `FireKampAtesi` | **20 sn** | Tek çağrı |
| `GrillFishes` | **60 sn (ortak!)** | Her `GrillFishes` çağrısında yeni timer |
| `CloseFisherShopPage` | **20 sn** | Tek çağrı |
| `InsertObjectToSkillSlots` (CharSpecialThings) | **90 sn** | Çağrı başında |

### Satın Alma Matematiği

- Hedef: **32 yığın × 200 solucan = 6.400 solucan** (4 shift sayfası × 8 slot = 32 slot).
- Sağ tık = 50 solucan → yığın başına **4 tık** → `neededWorms * 4` toplam tık.
- Bilinçli tasarım: hızlı erişimde her slot 200'lük yığın taşısın (200'lük `arrayWorm200`
  şablonuyla tanınır, "200" rakamı `array200WhiteNumber` ile okunur).

---

## 11. Tetikleyiciler — Ne Zaman Çağrılır?

| Tetikleyici | Konum | Çağrı |
|---|---|---|
| **Balık botu yeni başlatıldı** (oyun hazır, paneller kapalı) | `FishingHandle.cs:393` | `prepareFish.StartPrepareFishing()` |
| **"Yer yok" pembe mesajı** (envanter dolu) | `FishingHandle.cs:614` (`HandlePinkChat`) | önce `ThrowWorm()` (envanterdeki solucanı yere atıp onayla — yer/temizlik), sonra `StartPrepareFishing()` → ızgara ile yer açılır |
| **"Balık tutamazsın" pembe mesajı** | `FishingHandle.cs:621,630` | yalnız `GoToFishPlace()` (konum düzeltme) |
| **Hazırlık sürerken** | `ThreadsHandler` (Thread 3) | `isPrepareFishingStarted == true` iken chat/fısıltı işleri **atlanır** |
| **Mağaza kapatma ihtiyacı** | `CharSpecialThings.cs:211-214` | paylaşılan `PrepareFishing` instance'ı ile `CloseFisherShopPage()` |

---

## 12. Hata Senaryoları Özeti

| Durum | Tepki |
|---|---|
| Bot durduruldu (`isFishingStopped`) | Neredeyse her fonksiyon kendi bayrak kontrolünden `return` ile çıkar |
| Karakter öldü (`isCharKilled`) | Aynı — hazırlık yarıda kesilir; ana döngü ölüm yönetimi Thread 2'dedir |
| Balıkçı 40 sn'de bulunamadı | CHAR butonu + A/S/D yedek hareketi → tarama devam |
| Mağaza kapalıysa iş bekleniyorsa | `CheckFisherIsThere()` ile yeniden açılır (özyineleme) |
| Envanter dolu (satın alma sırasında) | "yer yok" dialog'u → ESC → satın alma kesilir |
| Kamp Ateşi 20 sn'de yerleştirilemedi | `Rectangle.Empty` → ızgara yapılmaz (üst akış loglar) |
| Balık 60 sn'de yanmadı | `GrillFishes → false` → balıkçı/shop/ateş yenilenip tekrar denenir |
| Ateşe gitmeyen eşya (yere atma dialog) | ESC + `false` → yeniden deneme döngüsü |
| Solucan 500 sn'de tamamlanamadı | `CombineAndCountWorms → false` → slot dizme/`GoToFishPlace` atlanır |
| Mağaza 20 sn'de kapanmadı | `CloseFisherShopPage` sessizce vazgeçer |

---

## 13. Bilinen Tuhaflıklar / Geliştirme Notları

(İleride ekleme/düzeltme yaparken işine yarayacak noktalar — kod olduğu gibi anlatılmıştır.)

1. **`CheckDelayTimeInSecond` ters okunur** (§2): `true` = süre var, `false` = zaman aşımı.
2. **`BuyKampAtasiFromFisher` çıkış kontrolü AND:** `if (isFishingStopped && isCharKilled) return;`
   — pratikte yalnızca ikisi birden true ise çıkar; muhtemelen `||` olmalı.
3. **`GrillFishingHandle` erken çıkışı eksik:** yalnızca
   `fishCoordinatesPageOne[0..2]` (yabbie/altın sudak/palamut) boş diye `return` eder;
   yalnız kurbağa/kadife seçiliyse ve balıklar sayfa 1'de olsa bile ızgara atlanabilir.
4. **`GrillFishes` içindeki 60 sn timer ortaktır ve resetlenmez:** çok balıkta zaman aşımı
   olursa iş yarıda kesilir; üst döngü balıkçı/shop/ateşi yenileyip devam eder.
5. **Yeniden deneme döngüsünde `CheckFisherIsThere()` sonucu kontrol edilmiyor**
   (`GrillFishingHandle`): false dönse de `CheckFisherShopPage()` denenir.
6. **`BuyKampAtasiFromFisher` else dalı:** `CheckFisherIsThere()` sonucu yok sayılır,
   koşulsuz özyineleme yapılır (mağaza hiç açılamazsa özyineleme derinleşebilir).
7. **`CheckFisherIsThere` W + özyineleme:** eşleşmediğinde ilerleyip kendini tekrar çağırır —
   sonsuz döngü riski yalnızca bayraklarla sınırlandırılmıştır.
8. **`GetFishTypesForGrilling` denizkızı içermiyor** (`isDenizkizSelected` hariç).
9. **`GrillFishes` yeniden denemelerinde slot rect'leri yeniden hesaplanmıyor**
   (dışarıdan gelen `rectPageOne/Two` sabit kalır) — slot konumları yerinde kaldığı için
   genelde çalışır; envanter tamamen değişirse yanlış slotlara tıklanabilir.
10. **İki tıklamalı "kullan" mekanizması:** sürükleme (mouse down/up) yerine
    "al + hedefe tık" kullanılır; oyunun eşyayı hedefe yönlendirmesi buna göre çalışır.

---

*Bu belge `PrepareFishing.cs` (877 satır) + ilgili `CharSpecialThings.cs`,
`FishingHandle.cs`, `TimerGame.cs`, `GameObjectCoordinates.cs`, `ColorGame.cs`
kodları incelenerek hazırlanmıştır.*
