# Swagger Dashboard

OpenAPI tabanlı merkezî API test, yönetim ve proxy platformu. Swagger UI'ın yerine geçer:
kullanıcı elindeki swagger bağlantısının başına dashboard adresini yazar, uygulama o adresi
ilk görüşünde dokümanı okuyup dashboard'u hazırlar ve saklar, sonraki her ziyarette hazır
sayfayı gösterir.

## Amaç

- Swagger bağlantılarını tek yerden yönetmek.
- OpenAPI dokümanını yalnızca ilk kayıtta veya güncellemede okumak; sonraki açılışlarda
  saklanan `DashboardJson`'u kullanmak.
- Tarayıcının hedef API'ye doğrudan istek göndermesini engellemek; tüm çağrıları backend
  proxy üzerinden geçirmek.
- Yeni bir API eklemek için kod değişikliği gerektirmemek.

## Desteklenen doküman sürümleri

| Sürüm | Durum |
| --- | --- |
| Swagger 2.0 | Destekleniyor |
| OpenAPI 3.0.x | Destekleniyor |
| OpenAPI 3.1.x | **Desteklenmiyor** |

Kullanılan okuyucu (`Microsoft.OpenApi.Readers` 1.6.x) 3.1'i okumuyor. 3.1 bir doküman
kaydedilmeye çalışıldığında uygulama sürümü adıyla söyleyerek reddeder; yarım bir dashboard
üretmez. Çoğu üretici hem 3.0 hem 3.1 çıktı verebiliyor; bu durumda 3.0 adresini kullanın.

Doküman içindeki `$ref` referansları çözümlenir. Başka bir dosyaya işaret eden referanslar
(`./common.yaml#/components/schemas/Order` gibi) çözümlenemez: indirilen tek bir dokümanın
yanında okunacak dosya yoktur. Bu alanlar formda JSON kutusu olarak, hangi referansın
çözümlenemediği yazılarak gösterilir ve dashboard uyarılarına eklenir.

## URL modeli

Kullanıcı normal swagger bağlantısının `https://` bölümünü dashboard adresiyle değiştirir:

```
https://api.company.com/swagger/index.html
        ↓
https://dashboard.company.com/api.company.com/swagger/index.html
```

Şema yazılmaz, çünkü nginx gibi ters vekiller varsayılan olarak path içindeki `//`
karakterini sıkıştırır ve gömülü `https://` bozulur. Şemalı ve sıkıştırılmış (`https:/host`)
biçimler de kabul edilip onarılır. Hedef `http` ise şema açıkça yazılmalıdır
(`.../http://api.company.com/swagger`), aksi halde adres sessizce `https`'e yükseltilirdi.

Yönetici isterse kısa bir takma ad tanımlar ve iki adres de aynı API'ye çözümlenir:

```
https://dashboard.company.com/customer-api
```

Paylaşılabilir endpoint bağlantısı:

```
https://dashboard.company.com/api.company.com/swagger/endpoint/get-customer-by-id
```

### Çözümleme akışı

```
İstek: /api.company.com/swagger
  │
  ├─ 1. Adres normalize edilir (host küçültülür, varsayılan port ve fragment atılır)
  ├─ 2. UrlKey = SHA-256(normalize edilmiş adres)
  ├─ 3. ApiUrlAliases tablosunda aranır (önce cache)
  │
  ├─ BULUNDU   → DashboardJson cache/DB'den döner.
  │               Swagger indirilmez, parse edilmez, hash karşılaştırılmaz.
  │
  └─ BULUNAMADI → yetki kontrolü → whitelist/SSRF kontrolü
                  → OpenAPI dokümanı keşfedilir ve indirilir
                  → doküman zaten kayıtlıysa yalnızca yeni adres alias olarak bağlanır
                  → değilse DashboardJson üretilir, hash hesaplanır, DB'ye yazılır
```

Bir API birden çok adresle açılabilir (`/swagger`, `/swagger/index.html`,
`/swagger/v1/swagger.json`). Kimlik dokümanın kendi adresidir; diğer yazımlar
`ApiUrlAliases` tablosunda tutulur, böylece hepsi tek kayda çözümlenir.

## Mimari

| Katman | İçerik |
| --- | --- |
| `SwaggerDashboard.Domain` | Varlıklar ve roller. Dış bağımlılığı yok. |
| `SwaggerDashboard.Application` | `DashboardDocument` modeli, OpenAPI ayrıştırıcı, hash, URL normalizasyonu, form ağacı, kod üretimi, servis sözleşmeleri. |
| `SwaggerDashboard.Infrastructure` | EF Core, SSRF korumalı HTTP hattı, proxy, cache, kimlik, arka plan işleri. |
| `SwaggerDashboard.Web` | Blazor Server arayüzü, kimlik doğrulama uçları, yapılandırma. |
| `SwaggerDashboard.Tests` | Birim ve entegrasyon testleri. |

Servisler: `IOpenApiDocumentService`, `IApiDefinitionService`, `IDashboardGeneratorService`,
`IApiProxyService`, `IRequestLogService`, `ISwaggerRefreshService`.

### DashboardJson ve şema sürümü

`DashboardDocument` sürümlenmiştir (`SchemaVersion`). Üretici çıktısının şekli değiştiğinde
`DashboardDocument.CurrentSchemaVersion` artırılır; saklanan sürümü eski olan kayıtlar ilk
okumada bir kez yeniden üretilir. Bu alan olmadan üretici değişikliği eski kayıtları sessizce
bozardı.

### Hash

Doküman önce kanonik JSON'a çevrilir (nesne alanları sıralanır, boşluk atılır, sayılar ham
metinden yazılır), sonra SHA-256 alınır. Kanonikleştirme olmadan hedef API alan sırasını
değiştirdiğinde hash değişir ve "yeniden oluşturma" mantığının tüm kazancı kaybolurdu.
Dizi sırası korunur, çünkü OpenAPI'de dizi sırası anlamlıdır.

### ApiEndpoints tablosu neden var

`DashboardJson` render kaynağıdır; `ApiEndpoints` arama, güncelleme farkı (diff) ve
raporlama kaynağıdır. İkisi aynı transaction içinde güncellenir.

### Arayüz ve tasarım token'ları

Tüm ölçüler ve renkler `wwwroot/app.css` başındaki token'lardan gelir: 4 piksellik boşluk
skalası (`--sd-1`…`--sd-6`), yazı skalası (`--sd-text-xs`…`--sd-text-2xl`), yüzeyler
(`--sd-surface-0/1/2`), kenarlıklar, vurgu rengi ve HTTP metot renkleri. Bir konsolda aynı
birkaç şekil (etiket, değer, rozet, satır) binlerce kez tekrarlanır; tek tek seçilen değerler
zamanla birbirinden ayrışır ve hiçbir şey hizalanmaz.

- **Renkler yalnızca token üzerinden.** Açık tema `:root`, koyu tema
  `[data-bs-theme="dark"]` bloğunda tanımlıdır. Başka bir yere yazılan sabit renk,
  yazıldığı temada doğru, diğerinde yanlış görünür.
- **Bootstrap aynı paleti kullanır.** `--bs-body-bg`, `--bs-border-color`, `--bs-code-color`
  gibi değişkenler token'lara bağlanır; böylece panel, uyarı, buton ve form kontrolleri elle
  yazılan bileşenlerle aynı zeminde durur.
- **Metot renkleri tek takımdır.** Beyaz metinle her iki temada da 4.5:1 kontrastı geçecek
  kadar koyu seçildiler, bu yüzden rozetin temaya göre değişmesi gerekmez. Renk ikincil
  ipucudur: metot adı her zaman yazıyla da durur.
- Klavye kullanıcısı için tek bir `:focus-visible` çerçevesi elle çizilen kontrollerde de
  (endpoint listesi satırları gibi) geçerlidir.

## Kurulum

### Gereksinimler

- .NET 8 SDK
- SQL Server (üretim) — geliştirmede SQLite yeterlidir

### Tek tıkla yerelde çalıştırma

| İşletim sistemi | Yapılacak |
| --- | --- |
| Windows | `baslat.cmd` dosyasına çift tıklayın |
| macOS / Linux | `./baslat.sh` |

Betik .NET 8 SDK'yı arar; bulursa uygulamayı doğrudan derleyip çalıştırır ve tarayıcıyı
açar. SDK yoksa Docker'a düşer (`docker-compose.local.yml`), o da yoksa hangisini
kurabileceğinizi söyler. İki yol da aynı yere çıkar:

```
http://localhost:5238      admin / development-only-password
```

Veriler SQLite'ta durur (`src/SwaggerDashboard.Web/swagger-dashboard.db`, Docker yolunda
adlandırılmış bir hacimde) ve kapanışta silinmez. Sıfırdan başlamak için:

```bash
./baslat.sh sifirla        # Windows: baslat.cmd sifirla
```

> `docker-compose.local.yml` yalnızca kendi makineniz içindir: Development modu her hedefe,
> `http`'ye ve özel ağlara izin verir. Sunucuya `docker-compose.yml` gider; o dosya SQL
> Server ayağa kaldırır ve hedef alan adı listesi verilmeden başlamayı reddeder.

### Elle çalıştırma

```bash
dotnet restore
dotnet build
cd src/SwaggerDashboard.Web
dotnet run
```

`appsettings.Development.json` SQLite kullanır (`Database:Provider = Sqlite`), şemayı
`EnsureCreated` ile oluşturur ve dış erişim kısıtlarını gevşetir. Bu ayarların üretimde
kullanılması uygulama tarafından reddedilir.

Geliştirme yöneticisi: `admin` / `development-only-password`.

### Veritabanı bağlantısı

`appsettings.json` içinde:

```json
"ConnectionStrings": {
  "SwaggerDashboard": "Server=...;Database=SwaggerDashboard;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True"
}
```

Üretimde parolayı dosyaya yazmayın; `ConnectionStrings__SwaggerDashboard` ortam değişkenini
veya bir secret store'u kullanın.

### Migration'lar

SQL Server migration'ların sahibidir ve uygulama açılışında otomatik uygulanır.

```bash
dotnet ef migrations add <Ad> \
  --project src/SwaggerDashboard.Infrastructure \
  --startup-project src/SwaggerDashboard.Web \
  --output-dir Persistence/Migrations
```

SQLite yalnızca geliştirme içindir ve migration çalıştırmaz; şemayı doğrudan oluşturur.

### İlk yönetici

`SwaggerDashboard:Seed:AdminUserName` ve `SwaggerDashboard:Seed:AdminPassword` ayarlanmışsa o
hesap oluşturulur. Parola verilmezse rastgele bir parola üretilir ve **yalnızca bir kez**
uygulama loguna yazılır. İlk girişten sonra değiştirin.

## Kullanım

### API ekleme

İki yol vardır:

1. **Otomatik (ilk ziyaret).** Admin veya Developer rolündeki bir kullanıcı prefix'li adrese
   gider; uygulama dokümanı bulur, dashboard'u üretir ve kaydeder.
2. **Yönetim panelinden.** `/admin/apis` → *Yeni API*. Ad, açıklama, route adı, taban adres
   ve izinli roller girilir. Kaydetmeden önce swagger adresine bağlanılır ve doküman
   ayrıştırılır; başarısız olursa kayıt oluşturulmaz.

Otomatik kayıt `SwaggerDashboard:Provisioning:AutoProvisionOnFirstVisit` ile kapatılabilir;
kapalıyken yalnızca yönetim panelinden kayıt yapılır.

### Route adı

Route adı tek segmentlik, harf/rakam/tire/alt çizgi içeren bir addır. Nokta içeremez (host
gibi yorumlanır) ve uygulamanın kendi yollarıyla (`admin`, `login`, `api`, `health`,
`_blazor`, …) çakışamaz. Catch-all route tüm siteyi kapsadığı için bu kontrol olmadan bir
takma ad yönetim panelini erişilemez hâle getirebilirdi.

### Örnek verilerle doldurma

Bir endpoint açıldığında istek formu **kendiliğinden dolu gelir**: önce dokümandaki `default`
ve `example` değerleri, sonra şemadaki tip ve format bilgisinden üretilen örnekler. Amaç, her
endpointin tek tıkla çalıştırılabilmesi. Üretilen değerler gönderilmeden önce formda görünür
ve değiştirilebilir; **Alanları temizle** hepsini boşaltır, **Örnek verilerle doldur** boş
kalanları yeniden doldurur.

Üretim kuralları:

- `uuid` gerçek bir GUID, `date-time` geçerli bir zaman damgası, `email` ve `uri` ise
  ayrılmış dokümantasyon alanlarını (`example.com`, `192.0.2.0/24`) kullanır — üretilen bir
  değer yanlışlıkla dışarı çıkarsa kimsenin sistemine gitmez.
- `enum` varsa ilk seçenek, sayısal alanlarda `minimum`/`maximum`, metinlerde
  `minLength`/`maxLength` uygulanır.
- `pattern` tanımlıysa üretilen değer desene karşı doğrulanır; uymuyorsa basit denemeler
  yapılır (posta kodu, sayısal kimlik gibi sabit uzunluklu desenler böyle karşılanır).
  Hiçbiri uymazsa alan boş bırakılır — geçersiz bir değerle doldurmak, boşluğu görünür
  bırakmaktan kötüdür.
- Girilmiş değerlerin üzerine yazılmaz, `readOnly` alanlar atlanır (yanıta aittirler),
  dosya alanları yükleme kontrolüne bırakılır.

### Toplu test

Sol menüdeki **Toplu test** butonu, API'nin bütün endpointlerini üretilen örnek verilerle
sırayla çağırır ve her birinin durumunu tek tabloda listeler: HTTP kodu, süre, yanıt boyutu.
Bir satıra tıklamak o endpointin kendi ekranını açar. Çağrılar seçili ortama gider, yani aynı
liste Test ve Production için ayrı ayrı alınabilir.

- **Yazan metotlar da çalışır.** POST, PUT, PATCH ve DELETE gerçekten gönderilir; hedefte
  veri oluşabilir, değişebilir, silinebilir. Panelin başındaki uyarı bunu söyler ve
  **Yalnızca okuma metotları** kutusu listeyi GET/HEAD/OPTIONS ile sınırlar. Varsayılan
  olarak hepsi dahildir: butonun amacı her endpointin durumunu görmek, yazanları sessizce
  dışarıda bırakmak başka bir sorunun cevabını verirdi.
- **Dosya yükleyen endpointler atlanır**, çünkü bir dosya için dürüst bir örnek içerik
  üretilemez. Zorunlu bir path parametresi için desene uyan değer üretilemediğinde de aynı
  şekilde atlanır ve sebebi satırda yazar. Uydurulmuş bir istekle "başarısız" raporlamak,
  kullanıcının düzeltemeyeceği bir hata üretmek olurdu.
- **Çağrılar sıralıdır.** Proxy istek başına scoped bir `DbContext` kullanır, paralel
  çağrılar onun üzerinde çakışır; ayrıca bütün endpointleri aynı anda göndermek, test
  ortamının çöküşünü toplu testin kendi sonucu hâline getirir. Sonuçlar geldikçe tabloya
  eklenir ve **Durdur** ile yarıda kesilebilir; o ana kadar alınan satırlar kalır.

### Kaydedilmiş istekler

Endpoint ekranında doldurulan form bir adla kaydedilebilir ve sonra tek tıkla geri yüklenir.
Kayıtlar kişiseldir: listeleme, yükleme ve silme her zaman oturumdaki kullanıcıya göre
filtrelenir, bu filtre ekranda değil serviste uygulanır.

- Kaydedilen şey **değerlerdir**, form ağacı değil. Ağaç swagger dokümanından türetilir ve
  doküman değişir; değer saklamak, alan eklenip çıktığında eski kaydın hâlâ yüklenebilmesi
  demektir — kalan alanlar dolar, olmayanlar sessizce atlanır.
- **Kimlik bilgileri kaydedilmez.** Onlar oturum boyunca sunucu belleğinde durur; bir bearer
  token'ı veritabanı satırına yazmak bu kolaylığı parola deposuna çevirirdi.
- Aynı ada tekrar kaydetmek öncekini değiştirir, böylece birbirinden ayırt edilemeyen iki
  satır oluşmaz. Endpoint başına 50 kayıt ve kayıt başına 128 KB sınırı vardır; sınırı aşan
  gövde sessizce kırpılmaz, reddedilir.
- Yükleme formu önce boşaltır: kayıtta olmayan alanlarda kalan örnek değerler isteğe eklenmiş
  olurdu ve kayıt, kaydedildiği isteği üretmezdi.

### İstek geçmişi

Endpoint ekranındaki **Son çağrılar**, o endpointe kendi yaptığınız son çağrıları durum kodu,
saat ve süreyle listeler. Bir satıra tıklamak o isteği forma geri yükler.

Log yeni bir alan kazanmadı: geri yükleme, loglanmış adresi okuyup değerleri çıkarır. Log,
giden çağrının denetim kaydıdır; form durumunu oraya kopyalamak, birbiriyle çelişebilen ikinci
bir gerçek üretirdi. Yol şablonu adresin **sonundan** eşleştirilir, çünkü adresin başı ortamın
taban yoludur ve ortamdan ortama değişir. Eşleşmeyen bir adres kısmen doldurulmaz, reddedilir.

Getirilenler path ve query değerleri ile gövdedir. Başlıklar ve kimlik bilgileri getirilmez —
loga maskelenerek yazılırlar. Toplu test çağrıları bu listede yer almaz (bkz. `IsBulkRun`).

### Yanıt karşılaştırma

Yanıt panelindeki **Fark** sekmesi, son yanıtı aynı endpointin bir önceki yanıtıyla alan alan
karşılaştırır: eklenen, silinen ve değişen alanlar yol (`$.musteri.adres.sehir`) ve iki
değeriyle listelenir. Durum kodu değiştiyse ayrıca söylenir.

- Karşılaştırma **yapısaldır, metin değil.** Yalnızca anahtar sırası veya boşluk farkı olan
  iki yanıt aynı yanıttır; metin karşılaştırması bu durumda gövdenin tamamını "değişti" diye
  gösterir ve aranan tek alanı gömerdi.
- Diziler **konuma göre** karşılaştırılır. Elemanları kimliğe göre eşlemek dokümanın vermediği
  bir anahtar gerektirir; onu tahmin etmek, yanlış tahmin edilen dokümanlarda kendinden emin
  ama hatalı bir sonuç üretirdi.
- Taban, çalıştırılan bir önceki yanıttır ve devrede tutulur; loglardan okunmaz. Yanıt
  gövdeleri yalnızca gövde loglaması açıkken saklanır, karşılaştırma ise her durumda
  çalışmalıdır. Endpoint değiştirildiğinde taban sıfırlanır — iki ilgisiz çağrının
  karşılaştırılması anlamsız olurdu.
- JSON olmayan gövdelerde alan bazlı karşılaştırma yapılmaz ve bu söylenir. Fark listesi
  300 satırla sınırlıdır; aşıldığında bu da yazılır.

### Dar ekran düzeni

900 pikselin altında üç panel yan yana sığmaz. Bu genişlikte çalışma alanı sekmeli hâle
gelir: **Endpointler / İstek / Yanıt**. Endpoint seçmek İstek sekmesine, çalıştırmak Yanıt
sekmesine geçirir — telefonda bir çağrının sonucunu görmek için listeyi ve formu aşağı
kaydırmak gerekmez. Toplu test açıkken Yanıt sekmesi görünmez, çünkü o düzende yanıt paneli
zaten yerini sonuç tablosuna bırakır.

Hangi düzenin geçerli olduğu CSS'e bırakılmıştır (`data-pane` + medya sorgusu); bu bir görünüm
alanı sorusudur ve JavaScript'in pencereyi ölçmesini gerektirmez. Geniş ekranda üç panel
eskisi gibi yan yanadır.

### Favoriler ve son kullanılanlar

Endpoint ekranındaki yıldız butonu endpointi kullanıcıya özel favorilere ekler; sol menüde
**★ Favoriler** ve **Son kullanılanlar** grupları listenin en üstünde çıkar ve aynı adla iki
filtre eklenir.

Son kullanılanlar ayrı bir tabloda tutulmaz, `ApiRequestLogs` üzerinden türetilir: her proxy
çağrısı zaten kullanıcı ve endpoint bilgisiyle oraya yazılıyor, ikinci bir kayıt yalnızca
senkron tutulacak fazladan bir şey olurdu. Bunun bedeli, listenin log saklama süresi kadar
geriye gitmesidir.

### İkili yanıtlar

Önizlenemeyen bir yanıt (PDF, Excel, görsel) indirme bağlantısı olarak sunulur. Dosya adı
hedefin `Content-Disposition` başlığından, yoksa yolun son parçasından, o da yoksa endpoint
adı ve içerik tipinden türetilir.

Baytlar Blazor devresinden değil, ayrı bir HTTP isteğiyle iner: megabaytlarca veriyi tek bir
JS interop argümanı olarak base64 ile geçirmek çalışmaz. Bağlantı tek kullanımlıktır, 10
dakika sonra düşer ve yalnızca isteği yapan oturuma açıktır.

### Ortam yönetimi

Bir API birden fazla adres üzerinden çağrılabilir: Development, Test, PreProd, Production.
`/admin/apis/{id}` ekranındaki **Ortamlar** tablosundan ortam eklenir, düzenlenir ve silinir;
dashboard'un sol üstündeki seçici bu listeden beslenir ve seçilen ortamın taban adresi proxy
çağrısında kullanılır. API ilk kez otomatik oluşturulduğunda swagger adresinden türetilen tek
bir `Default` ortamı yazılır.

Kurallar:

- Her zaman tam olarak bir ortam varsayılandır. İlk eklenen ortam istenmese de varsayılan olur
  (aksi hâlde dashboard hiçbir şey seçili olmadan açılırdı), varsayılanın işareti kaldırılırsa
  bir diğeri devralır, varsayılan silinirse kalanlardan biri devralır.
- Son ortam da silinebilir; o durumda çağrılar API'nin kendi taban adresine düşer.
- Ortam adı bir API içinde benzersizdir ve en fazla 64 karakterdir.
- Taban adres kaydedilirken de whitelist'e takılır. Aynı kural çağrı anında zaten
  uygulanıyor, ama hatayı yapıldığı ekranda söylemek gerekir; yoksa yanlış ortam sessizce
  kaydedilir ve ilk istekte anlaşılmaz bir hataya dönüşür. Kaydetme sırasında yalnızca ad
  çözümlemesi gerektirmeyen kısımlar denetlenir — bir ortam çoğu zaman var olmadan önce
  tanımlanır.

### Swagger yenileme

`/admin/apis` ekranında:

- **Swagger Güncelle** — dokümanı yeniden indirir, kanonik hash'i karşılaştırır. Hash aynıysa
  hiçbir şey yeniden üretilmez ve "değişiklik bulunmadı" bildirilir. Farklıysa dashboard ve
  endpoint kayıtları güncellenir, eklenen/silinen/değişen endpointler raporlanır, cache
  temizlenir.
- **Dashboard Yeniden Oluştur** — hash kontrolünden bağımsız çalışır ve saklanan ham
  dokümandan üretir; hedef API'nin erişilebilir olmasını gerektirmez.

Dashboard yalnızca şu durumlarda yeniden üretilir: bu iki işlem, hash değişikliği, saklanan
`DashboardJson`'un okunamaması veya şema sürümünün eskimesi.

### Zamanlanmış swagger kontrolü

`SwaggerDashboard:Provisioning:AutoRefreshHours` verilirse, aktif tüm API'lerin dokümanı bu
aralıkla arka planda yeniden kontrol edilir. Yapılan iş butonunkiyle aynıdır: önce hash
karşılaştırılır, doküman gerçekten değişmedikçe hiçbir şey yeniden üretilmez.

- **Varsayılan olarak kapalıdır.** Açık olsaydı uygulama, kimsenin istemediği bir anda kayıtlı
  her hedefe dışarı çağrı yapmaya başlardı; bunu isteyen operatör bilerek açar.
- Pasif API'ler kontrol edilmez.
- API'ler arasında `AutoRefreshDelaySeconds` (varsayılan 2sn) beklenir. Elli dokümanı tek
  seferde çekmek karşı taraf için tarayıcı botu gibi görünür; kimse sonucu beklemediği için
  bu gecikmenin maliyeti yoktur.
- Bir hedefe ulaşılamaması taramayı durdurmaz; kalanlar kontrol edilir ve o API bir sonraki
  turda yeniden denenir.
- İlk tarama uygulama açılışında değil, bir aralık sonra çalışır: açılış anı, her hedefe çağrı
  yapmak için uygulamanın en hazırlıksız olduğu andır ve her dağıtımda tekrarlanırdı.
- Bu şekilde yapılan güncellemeler denetim alanlarına `otomatik-kontrol` aktörüyle yazılır,
  böylece kimsenin hatırlamadığı bir değişiklik zamanlamaya bağlanabilir.

### Proxy akışı

```
Tarayıcı → Dashboard (Blazor Server devresi) → Dashboard backend → Hedef API
```

Tarayıcı hiçbir zaman bir URL belirtmez; API tanımının kimliğini ve endpoint slug'ını
gönderir. Hedef adres sunucu tarafında saklanan taban adres ile dokümandaki path şablonundan
kurulur, path değerleri segment olarak kaçışlanır. Aksi hâlde proxy genel amaçlı bir
yönlendiriciye dönerdi.

Aktarılmayan hop-by-hop headerlar: `Connection`, `Keep-Alive`, `Proxy-Authenticate`,
`Proxy-Authorization`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade` (ayrıca `Host` ve
`Content-Length`).

### Kimlik bilgileri

Hedef API'nin token/parola/API key değerleri **yalnızca sunucu belleğinde**, kullanıcı ve API
başına, kayan bir süreyle tutulur. Veritabanına yazılmaz ve tarayıcıya gönderilmez. Bunlar
dashboard kullanıcısının kimliğinden ayrıdır.

Desteklenen yöntemler: Bearer token, Basic, API Key ve **OAuth2 client credentials**.

OAuth2'de token'ı platform kendisi alır (`grant_type=client_credentials`) ve isteğe bearer
olarak ekler:

- **Yalnızca client credentials akışı desteklenir.** Diğer akışlar tarayıcı yönlendirmesi ve
  onay ekranıyla biter; sunucu tarafında çalışan bir test aracı bunu kullanıcı adına
  tamamlayamaz.
- Token adresi ve scope, dokümandaki `clientCredentials` tanımından okunup forma önerilir ama
  düzenlenebilir kalır; doküman eski veya hatalı olabilir.
- Client ID ve secret **Authorization başlığında** gönderilir (RFC 6749 §2.3.1); gövdeye de
  koymak secret'ı bir kez daha tel üzerine çıkarmaktan başka işe yaramaz.
- Token adresi de diğer hedefler gibi whitelist ve SSRF kontrolünden geçer. Bu olmadan
  whitelist'li bir platform, client secret'ını rastgele bir sunucuya POST etmeye ikna
  edilebilirdi.
- Token, süresi dolmadan 30 saniye öncesine kadar bellekte tutulur. Cache anahtarı kullanıcı,
  token adresi, client ID, scope ve **secret'ın özetini** içerir: secret değiştiğinde eskisiyle
  alınmış token'ın kullanılması, düzeltilmiş bilgilerin denenmeden "çalışıyor" görünmesi
  demek olurdu.

### Roller

| Rol | Yetki |
| --- | --- |
| `Admin` | Tüm yönetim, log ve kullanıcı işlemleri. |
| `Developer` | Endpoint çalıştırma ve yeni swagger adresini otomatik kaydetme. |
| `Tester` | Yalnızca kayıtlı API'lerde endpoint çalıştırma. |
| `ReadOnly` | Yalnızca görüntüleme. |

Bir API'ye `AllowedRoles` verilirse yalnızca o roller (ve `Admin`) görebilir.

## Güvenlik

Prefix'li URL modeli hedefi kullanıcının belirlemesine izin verdiği için dış erişim politikası
opsiyonel bir sıkılaştırma değil, modelin ayakta durma şartıdır.

- **Alan adı whitelist'i** (`SwaggerDashboard:Outbound:AllowedHostSuffixes`). Etiket sınırında
  eşleşir, yani `company.com` girdisi `evil-company.com`'u kapsamaz. Üretimde boş liste veya
  `AllowAnyHost` ile uygulama açılışta durur.
- **Private IP / loopback / link-local engeli.** `169.254.169.254` (bulut metadata) dâhil.
  Bir ad hem genel hem iç adrese çözümleniyorsa tamamen reddedilir.
- **DNS rebinding koruması.** Politika kontrolü ile bağlantı arasındaki pencere,
  `SocketsHttpHandler.ConnectCallback` içinde soketin gerçekten bağlanacağı IP yeniden
  doğrulanarak kapatılır.
- **Protokol kontrolü.** Varsayılan yalnızca HTTPS; `http` açıkça açılmalıdır. `ftp:`,
  `file:` gibi şemalar reddedilir.
- **Yönlendirme sınırı.** Yönlendirmeler elle izlenir ve her adım yeniden doğrulanır.
- **Boyut ve süre sınırı.** Doküman ve proxy yanıtı için bayt tavanı okuma sırasında
  uygulanır (`Content-Length` yalnızca ipucudur), istek zaman aşımı ve `CancellationToken`.
- **Geçersiz sertifika reddi.**
- **Otomatik kayıt için kimlik doğrulama ve saatlik oran sınırı.** Giriş ucunda ayrı sınır.
- **Secret maskeleme.** `Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, `Client-Secret`,
  `Password` loglarda maskelenir; `Authorization` değerinin yalnızca şeması görünür
  (`Bearer ***`). Üretilen kod örneklerinde gizli değerler `<TOKEN>` ile değiştirilir.
- **HTML yanıt önizlemesi** izole bir `sandbox` iframe içinde gösterilir; script çalışmaz.
- **CSRF** (antiforgery), güvenli headerlar, HttpOnly/Secure çerez, açık yönlendirme koruması
  (login `returnUrl` yalnızca site-içi yollara izin verir).
- **Kullanıcıya stack trace gösterilmez**; ayrıntılar `ILogger` ile kaydedilir.

### Whitelist yapılandırması

```json
"SwaggerDashboard": {
  "Outbound": {
    "AllowedHostSuffixes": [ "company.com", "internal-api.company.net" ],
    "AllowInsecureHttp": false,
    "AllowPrivateNetworks": false,
    "TimeoutSeconds": 30,
    "MaxRedirects": 3,
    "MaxDocumentBytes": 8388608,
    "MaxProxyResponseBytes": 16777216
  }
}
```

`AllowAnyHost` ve `AllowPrivateNetworks` yalnızca `Development` ortamında kullanılabilir;
başka bir ortamda uygulama başlatılmaz.

## Loglama

Her proxy çağrısı için kullanıcı, API, endpoint, method, URL, başlangıç/bitiş, süre, status,
IP, başarı ve hata mesajı yazılır. Gövdeler kişisel veri taşıdığı için varsayılan olarak
**yazılmaz**; `SwaggerDashboard:Logging:PersistBodies` ile açılır, açıldığında
`MaxLoggedBodyChars` ile kırpılır ve kırpıldığı işaretlenir. `RetentionDays` süresini geçen
kayıtlar arka plandaki bakım işi tarafından silinir.

## Cache

Cache anahtarı `swagger-dashboard:{apiDefinitionId}:{swaggerHash}`; ayrıca adres → API
eşlemesi ayrı anahtarlarla tutulur, böylece her ziyarette veritabanına gidilmez. Önce cache,
sonra veritabanı okunur. Swagger güncelleme, yeniden oluşturma, pasifleştirme, düzenleme ve
silme işlemlerinde ilgili API'nin tüm girdileri temizlenir. İlk sürüm `IMemoryCache`
kullanır; `IDashboardCache` arayüzü Redis'e geçişi çağıranları değiştirmeden mümkün kılar.

`/admin/apis` ekranındaki **Cache temizle** butonu bellekteki tüm dashboard ve route
kayıtlarını atar ve kaç kayıt atıldığını söyler — sayısı olmayan bir "temizlendi" mesajı, hiç
çalışmayan bir butondan ayırt edilemez. Hedefli temizleme zaten yenileme akışlarında
yapılıyor; bu buton, uygulamanın bilmediği bir değişiklikten sonra bir sonraki isteğin
veritabanından yeniden okumasını istediğiniz durum için var. Saklanan hiçbir veri silinmediği
için onay sormaz.

## Testler

```bash
dotnet test
```

Kapsam: OpenAPI ayrıştırma (`$ref`, özyineleme, `allOf`, `oneOf`, enum, deprecated, güvenlik
şemaları, multipart), `DashboardJson` üretimi, kanonik hash, URL normalizasyonu ve route
çözümleme, otomatik kayıt ve eşzamanlılık, rol/yetki kuralları, SSRF politikası, proxy istek
kurulumu, dinamik form ağacı, maskeleme, kod üretimi, swagger yenileme farkı, örnek veri
üretimi, toplu test, kaydedilmiş istekler, geçmişten geri yükleme, yanıt karşılaştırma,
OAuth2 client credentials ve gerçek bir HTTP hedefine karşı uçtan uca akış.

### Bileşen testleri

Arayüz kuralları (arama ve filtreler, alan doğrulama işaretleri, sekmelerin gösterdiği içerik,
maskeleme) bileşenin kendisinde yaşar. Bunlar [bUnit](https://bunit.dev) ile bileşen tek başına
render edilerek doğrulanır: aynı kuralları tarayıcı sürerek denemek uygulamanın tamamının ayakta
olmasını gerektirir ve saniyeler sürer; burada milisaniyeler sürüyor ve hata doğrudan ilgili
bileşeni gösteriyor. Tarayıcı testleri yerine geçmez — onlar hâlâ akışların gerçekten uçtan uca
çalıştığını gösteriyor.

## Docker

```bash
export MSSQL_SA_PASSWORD='Strong_Passw0rd!'
export ALLOWED_HOST_SUFFIX='company.com'
export ADMIN_PASSWORD='...'
docker compose up --build
```

Uygulama `http://localhost:8080` adresinde çalışır. Konteyner ayrıcalıksız kullanıcıyla
çalışır ve `/health` ucunu kendi kendine yoklar.

## Yayınlama

### Yayına açmadan önce zorunlu dört ayar

| Ayar | Neden |
| --- | --- |
| `SwaggerDashboard:Outbound:AllowedHostSuffixes` | Boşsa uygulama zaten başlamaz. Prefix'li URL modeli hedefi kullanıcının belirlemesine izin verdiği için bu liste açık proxy olmayı engelleyen kontroldür. |
| `SwaggerDashboard:Hosting:BehindReverseProxy` | TLS'i kenarda sonlandıran her platformda gerekir. Olmadan audit logdaki istemci IP'si vekilin adresi olur, paylaşılabilir bağlantılar iç adresi gösterir ve `HTTPS_PORT` tanımlıysa yönlendirme döngüsü oluşur. |
| `SwaggerDashboard:Hosting:DataProtectionKeyPath` | Kalıcı disk üzerinde olmalı. Yoksa her dağıtımda anahtarlar yenilenir; tüm oturumlar düşer ve giriş formu antiforgery hatası verir. |
| `SwaggerDashboard:Access:RequireAuthenticationToView` | İnternete açık dağıtımlarda `true` yapın. Varsayılan `false`, iç ağa uygundur; açıkken URL'yi öğrenen herkes iç API'lerinizin endpoint listesini okuyabilir. |

İlk yönetici parolasını `SwaggerDashboard:Seed:AdminPassword` ile verin; vermezseniz rastgele
bir parola üretilir ve yalnızca bir kez konteyner loguna yazılır.

### Fly.io

Depoda hazır bir `fly.toml` var. `AllowedHostSuffixes` değerini kendi API alan adınızla
değiştirdikten sonra:

```bash
fly launch --no-deploy --copy-config      # uygulama adını ve bölgeyi seçin
fly volumes create swagger_dashboard_data --size 1
fly secrets set SwaggerDashboard__Seed__AdminPassword='...'
fly deploy
```

Blazor Server her açık sekme için canlı bir devre tutar ve SQLite tek yazar kabul eder, bu
yüzden yapılandırma tek makineyi ayakta tutacak şekilde ayarlıdır (`auto_stop_machines` kapalı,
`min_machines_running = 1`). Ölçeklenmeniz gerekirse önce SQL Server'a geçin ve cache'i Redis'e
taşıyın.

### Kendi sunucunuzda Docker ile

```bash
export MSSQL_SA_PASSWORD='...'
export ALLOWED_HOST_SUFFIX='company.com'
export ADMIN_PASSWORD='...'
docker compose up --build -d
```

Önüne TLS sonlandıran bir ters vekil koyun (nginx, Caddy, Traefik) ve
`SwaggerDashboard__Hosting__BehindReverseProxy=true` verin. Vekil WebSocket trafiğine izin
vermelidir; Blazor Server bunu gerektirir.

Konteyner root olarak başlar, bağlı diskin sahipliğini düzeltir ve uygulamayı ayrıcalıksız
kullanıcıya (uid 10001) düşürerek çalıştırır. Platformların diski root sahipliğiyle bağlaması
aksi hâlde uygulamanın veritabanını yazamamasına yol açardı.

## Üretim dağıtımı

- HTTPS zorunlu; uygulama HSTS ve güvenli çerez politikası uygular. TLS sonlandırma ters
  vekilde yapılıyorsa `ForwardedHeaders` yapılandırın.
- `AllowedHostSuffixes` mutlaka doldurulmalıdır.
- Bağlantı dizesi ve ilk yönetici parolası ortam değişkeni veya secret store ile verilmelidir.
- Ters vekil kullanıyorsanız WebSocket (SignalR) trafiğine izin verin; Blazor Server bunu
  gerektirir.
- `Provisioning:MaxProvisionsPerUserPerHour` ve `Logging:RetentionDays` değerlerini kurumsal
  politikanıza göre ayarlayın.
- Deployment pipeline'ından yenileme için yönetim panelindeki Swagger Güncelle işlemi
  kullanılır; hash aynıysa hiçbir şey yeniden üretilmez.

## Örnek doküman

`samples/customer-api-swagger.json` — GET/POST/PUT/PATCH/DELETE, enum, dizi, iç içe nesne,
`allOf`, `oneOf` + discriminator, özyinelemeli şema, dosya yükleme ve iki güvenlik şeması
içerir. Testler bu dosyayı kullanır.
