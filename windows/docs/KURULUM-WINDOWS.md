# On Air — Windows Kurulum ve "Windows engelliyor" Rehberi

Bu dosya, `OnAirIndicator.exe` dosyasını sana gönderdiysem yanında gelen kurulum rehberidir.
Uygulama imzalıdır ama **ticari bir sertifikayla değil**, kendi ürettiğim bir sertifikayla. Bu yüzden
Windows ilk çalıştırmada bir şeyler sorabilir. Aşağıda her uyarının ne olduğu, hangisinin gerçekten
önemli olduğu ve ne yapman gerektiği yazıyor.

---

## Hızlı kurulum

Sana gelen klasörde üç dosya olmalı: `OnAirIndicator.exe`, `OnAir-Publisher.cer`,
`trust-onair-cert.ps1`.

```powershell
# 1) Klasöre gir, PowerShell aç (yönetici GEREKMİYOR)
cd <klasör>

# 2) Hazırlık betiği: sertifikayı güven, indirme bayrağını temizle, imzayı doğrula
powershell -ExecutionPolicy Bypass -File .\trust-onair-cert.ps1

# 3) Kur
.\OnAirIndicator.exe
```

Betik bir onay penceresi açacak (aşağıdaki [güvenlik notu](#güvenlik-notu--neye-rıza-gösteriyorsun)),
sonra da bu makinede Akıllı Uygulama Denetimi'nin açık olup olmadığını söyleyecek.

Geri almak istersen: `.\trust-onair-cert.ps1 -Remove`

---

## Karşılaşabileceğin iki farklı uyarı

Bunlar **aynı şey değil** ve çözümleri de farklı. Ekrandaki başlığa bak:

| | **SmartScreen** | **Akıllı Uygulama Denetimi (SAC)** |
|---|---|---|
| Başlık | "Windows bilgisayarınızı korudu" | "Akıllı Uygulama Denetimi, güvenli olmayabilecek bir uygulamayı engelledi" |
| Butonlar | "Daha fazla bilgi" → **"Yine de çalıştır"** | Sadece "Tamam" ve "Store'dan uygulama alın" |
| Aşabilir misin? | **Evet** | **Hayır** — çalıştırma seçeneği yoktur |
| Ne kadar yaygın? | İnternetten indirilen her imzasız dosyada | Nadir — aşağıya bak |

### SmartScreen — "Windows bilgisayarınızı korudu"

Bu uyarı, dosyanın **internetten indirildiği** işaretine (Mark-of-the-Web) bağlıdır. O işaret
silinirse uyarı hiç çıkmaz.

`trust-onair-cert.ps1` bunu senin için yapıyor. Elle yapmak istersen:

```powershell
Unblock-File .\OnAirIndicator.exe
```

Alternatif olarak uyarı ekranında **"Daha fazla bilgi" → "Yine de çalıştır"** diyebilirsin; ya da
dosyaya sağ tık → **Özellikler** → alttaki **"Engellemeyi kaldır"** kutusunu işaretle → Tamam.

### Akıllı Uygulama Denetimi (SAC) — "…engelledi"

Bunun **"yine de çalıştır" seçeneği yoktur.** Ve önemli bir nokta:

> **Kendinden imzalı sertifika SAC'ı geçmez.** Sertifikayı Güvenilen Kök'e eklemek de işe yaramaz.

Bu bir tahmin değil, 5 Ağustos 2026'da geliştirme makinesinde ölçüldü: EXE kendinden imzalı
sertifikayla imzalandı, sonra **imzasız** bir kontrol kopyasıyla yan yana çalıştırıldı — ikisi de
aynı davrandı, yani imzanın hiçbir etkisi olmadı. Sebebi şu: SAC'ın kuralı "geçerli imzası olsun"
değil, **"doğrulanmış VE itibarlı" (verified *and* reputable)**. Politikanın kendisi Microsoft
tarafından imzalanmıştır, değiştirilemez ve senin makinendeki güven deposuna değil Microsoft'un
bulut itibar servisine (ISG) bakar. Kendinden imzalı bir sertifika herkese açık bir zincire
bağlanmadığı için orada hiç karşılığı olamaz.

**Önce: bu makinede SAC var mı?**

```powershell
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
# çıktı yok / 0 = kapalı, sorun yok
# 1              = açık (zorlayıcı)
# 2              = değerlendirme modu
```

**Çoğu bilgisayarda hiç çıktı gelmez.** SAC yalnızca **temiz kurulmuş** Windows 11 22H2 ve
üzerinde bulunur; Windows 10'dan yükseltilen veya kurumsal imajdan gelen makinelerde bu özellik
Windows Güvenliği'nde hiç görünmez. Yani bu uyarıyı görme ihtimalin düşük.

**Engellendiysen iki seçeneğin var:**

**a) Bekle.** Engelleme kutusundaki "Microsoft, uygulamanın güvenli olup olmadığını doğrulamak için
inceleyecek" cümlesi gerçekten işliyor. Geliştirme makinesinde ilk engellemeden **yaklaşık 30 dakika
sonra** dosya kendiliğinden çalışır hale geldi — SAC hâlâ açıkken, dosya hâlâ değişmemişken. Hiçbir
şey yapmadan bir süre sonra tekrar dene.

**b) SAC'ı kapat.** Windows Güvenliği → **Uygulama ve tarayıcı denetimi** → **Akıllı Uygulama
Denetimi ayarları** → **Kapalı**.

> Eskiden bu geri dönüşü olmayan bir işlemdi (tekrar açmak için Windows'u yeniden kurmak
> gerekiyordu). **Nisan 2026 güncellemesinden itibaren bu kısıt kaldırıldı** — istediğin an
> "Açık"a geri alabilirsin.

---

## Kendinden imzalı sertifika ne yapar, ne yapmaz

Yanlış beklentiyle vakit kaybetmemen için net liste:

**Yapar:**
- Dosya Özellikler'inde ve UAC pencerelerinde "Bilinmeyen yayımcı" yerine yayımcı adını gösterir.
- **Kurcalanmayı yakalar** — imzalandıktan sonra dosyada tek bayt değişse imza geçersiz olur. Sana
  ulaşan dosyanın yolda değiştirilmediğini doğrulayabilirsin.
- `Get-AuthenticodeSignature` çıktısını `Valid` yapar (sertifikayı güvendikten sonra).

**Yapmaz:**
- SmartScreen itibarı sağlamaz. (SmartScreen'i çözen şey `Unblock-File`, sertifika değil.)
- Akıllı Uygulama Denetimi'ni geçmez. (Yukarıda ölçümle gösterildi.)
- Sertifikayı içe aktarmayan bir makinede hiçbir işe yaramaz.

Bunların hepsini gerçekten çözen tek şey, herkese açık bir kök sertifika yetkilisinden alınmış
**EV kod imzalama sertifikasıdır** (~250-400 $/yıl). Bu proje için şimdilik alınmadı.

---

## Güvenlik notu — neye rıza gösteriyorsun

`trust-onair-cert.ps1` sertifikayı **"Güvenilen Kök Sertifika Yetkilileri"** deposuna ekliyor.
Bunun anlamı: bu bilgisayar, o sertifikanın özel anahtarıyla imzalanmış **her** yazılıma güvenecek —
sadece bu uygulamaya değil.

Bu yüzden:

- Windows sana bir onay penceresi gösterecek. **O pencere engel değil, işin ta kendisi.** Tanımadığın
  birinden gelen bir sertifika için onaylama.
- Betik başlarken sertifikanın **parmak izini (thumbprint)** yazdırıyor. Dosyanın geldiği kanaldan
  **farklı** bir kanaldan (telefon, yüz yüze) göndericiyle karşılaştır.
- İşin bittiğinde geri al: `.\trust-onair-cert.ps1 -Remove`
- Özel anahtar hiçbir zaman dağıtılmaz — sana gelen `.cer` dosyası yalnızca genel anahtarı içerir,
  onunla bir şey imzalanamaz.

---

## Teşhis — "engellendi mi, yoksa başka bir sorun mu?"

Windows'un dosyayı gerçekten kod bütünlüğü politikasıyla engelleyip engellemediğini kesin olarak
görmek için:

```powershell
Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 20 |
  Where-Object Id -eq 3077 |
  Select-Object TimeCreated, Message
```

Burada `OnAirIndicator.exe` geçen bir kayıt varsa engellenmiştir. Kayıttaki
`Policy ID: {0283ac0f-fff1-49ae-ada1-8a933130cad6}` değeri SAC politikasını gösterir.

Uygulama çalışıyor ama beklendiği gibi davranmıyorsa engelleme değil, uygulama sorunudur — logları
oku:

```
%LOCALAPPDATA%\OnAir\logs\onair-YYYY-AA-GG.log
```

Ayrıntılı çıktı için `%LOCALAPPDATA%\OnAir\config.json` içinde `"logLevel": "debug"` yap ve
uygulamayı yeniden başlat. Cihaz bağlantısı, eşleştirme ve ışığın yanmaması gibi konular için ana
[`README.md`](../README.md) dosyasındaki **Troubleshooting** bölümüne bak.

---

## Başka bir bilgisayara kurarken

> **En önemli nokta, Windows uyarıları değil:** cihaz **tek bir bilgisayarla** eşleşir. Firmware
> tek-host çalışır ve bir bilgisayar bağlandığı anda **yayın yapmayı bırakır**. Yani cihaz eski
> bilgisayarına bağlıyken yeni bilgisayar onu **hiç göremez** — "cihaz bulunamadı" hatası alırsın.
> İki bilgisayarda aynı anda kullanılamaz; taşımak gerekir.

### Neye ihtiyacın var

- Windows 10/11, **Bluetooth 4.0+ (BLE)** olan bir bilgisayar ve Bluetooth açık.
- Yanına alacağın dosyalar: `OnAirIndicator.exe`, `OnAir-Publisher.cer`, `trust-onair-cert.ps1`.
- .NET kurmana **gerek yok** — EXE self-contained, runtime'ı içinde taşıyor.
- Yönetici hakkı **gerekmiyor** — her şey `%LOCALAPPDATA%\OnAir` altına kuruluyor.

### Sıra

**1) Eski bilgisayarı bırak (atlanırsa eşleştirme başarısız olur)**

Eski bilgisayarda uygulamayı kaldır:

```powershell
.\OnAirIndicator.exe --uninstall
```

Bu; uygulamayı durdurur, otomatik başlatmayı siler, **cihazın eşleşmesini kaldırır** ve
`%LOCALAPPDATA%\OnAir` klasörünü siler. Çalıştırdığın EXE yerinde kalır.

Eski bilgisayara erişemiyorsan sorun değil — 2. adım zaten bağı cihaz tarafından koparıyor.

**2) Cihazın eşleşme kayıtlarını temizle**

Cihazın üstündeki **BOOT** düğmesini **~3 saniye** basılı tut. Bu tüm bond kayıtlarını siler ve
cihazı yeniden yayın (advertising) moduna sokar. Bu adım olmadan cihaz yeni bilgisayara görünmez.

`DEBUG_SERIAL` açıksa seri monitörde `[BLE]` satırlarından yayına geçtiğini doğrulayabilirsin.

**3) Yeni bilgisayarı hazırla**

```powershell
cd <dosyaların olduğu klasör>
powershell -ExecutionPolicy Bypass -File .\trust-onair-cert.ps1
```

Betik sertifikayı güvenir, indirme bayrağını temizler ve o makinede Akıllı Uygulama Denetimi'nin
açık olup olmadığını söyler. Engellenirsen yukarıdaki
[SAC bölümüne](#akıllı-uygulama-denetimi-sac--engelledi) bak.

**4) Kur**

```powershell
.\OnAirIndicator.exe
```

Açılan pencereden **Install**. Uygulama kendini `%LOCALAPPDATA%\OnAir` altına kopyalar, oturum
açılışına ekler ve hemen çalışmaya başlar.

**5) Eşleştir ve doğrula**

Kurulum sonrası uygulama cihazı servis UUID'sinden bulup **otomatik eşleşir** — JustWorks bonding
olduğu için ekranda hiçbir onay çıkmaz. Bulunan MAC adresi `config.json`'a yazılır.

Kamerayı aç → ışık yanmalı; kapat → sönmeli. Tepsi ikonuna sağ tık → **Status** ile bağlantı
durumunu görebilirsin. Sorun varsa log:

```
%LOCALAPPDATA%\OnAir\logs\onair-YYYY-AA-GG.log
```

`Camera/mic state = 0x01` satırı yalnızca kameranın algılandığını gösterir; cihaza gerçekten
yazıldığını anlamak için **eşleşen** `State write 0x01 ok (status=Success)` satırını ara.

### Sık karşılaşılan durum: "cihaz bulunamadı"

Neredeyse her zaman 2. adımın atlanmış olmasındandır — cihaz hâlâ başka bir bilgisayara bonded
durumdadır ve yayın yapmıyordur. BOOT düğmesini ~3 sn basılı tut, sonra tepsiden **Re-pair device**
de. Ayrıntı için ana [`README.md`](../README.md) → *Troubleshooting*.

### Eski bilgisayara geri dönmek istersen

Aynı sıra ters yönde işler: yeni bilgisayarda `--uninstall`, cihazda BOOT ~3 sn, eski bilgisayarda
kurulum. `config.json` içindeki `deviceMac` alanını elle taşımana gerek yok — eşleştirmeden sonra
kendisi doluyor.

---

## Geliştirici tarafı (yeni sürüm yayınlarken)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

.\scripts\sign-selfsigned.ps1
```

Betik sertifikayı ilk seferde üretir, sonraki sürümlerde **aynısını** yeniden kullanır — böylece
ekip `.cer` dosyasını bir kez içe aktarır. Çıktıda basılan SHA-256 değerini indirme bağlantısının
yanında yayınla.

**Her yeni derlemede dosyanın hash'i değişir**, dolayısıyla önceki sürümün kazandığı SAC/SmartScreen
itibarı yeni sürüme geçmez. SAC'lı bir makinede yeni sürüm yine engellenebilir; yukarıdaki (a) ve
(b) seçenekleri geçerlidir.
