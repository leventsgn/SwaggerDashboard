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

## Kurulum

### Gereksinimler

- .NET 8 SDK
- SQL Server (üretim) — geliştirmede SQLite yeterlidir

### Geliştirme ortamında çalıştırma

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

## Testler

```bash
dotnet test
```

Kapsam: OpenAPI ayrıştırma (`$ref`, özyineleme, `allOf`, `oneOf`, enum, deprecated, güvenlik
şemaları, multipart), `DashboardJson` üretimi, kanonik hash, URL normalizasyonu ve route
çözümleme, otomatik kayıt ve eşzamanlılık, rol/yetki kuralları, SSRF politikası, proxy istek
kurulumu, dinamik form ağacı, maskeleme, kod üretimi, swagger yenileme farkı ve gerçek bir
HTTP hedefine karşı uçtan uca akış.

## Docker

```bash
export MSSQL_SA_PASSWORD='Strong_Passw0rd!'
export ALLOWED_HOST_SUFFIX='company.com'
export ADMIN_PASSWORD='...'
docker compose up --build
```

Uygulama `http://localhost:8080` adresinde çalışır. Konteyner ayrıcalıksız kullanıcıyla
çalışır ve `/health` ucunu kendi kendine yoklar.

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
