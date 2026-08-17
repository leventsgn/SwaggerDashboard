# Hazır Windows paketi

`SwaggerDashboard-Windows.zip` — kurulum gerektirmeyen, çalışmaya hazır Windows sürümü.

1. Zip'i bir klasöre çıkarın.
2. `SwaggerDashboard-Baslat.bat` dosyasına çift tıklayın.
3. Tarayıcı açılır: <http://localhost:5238> — `admin` / `development-only-password`

.NET, Docker veya internet bağlantısı gerekmez: paket kendi .NET çalışma zamanını taşır.
Kaydettiğiniz her şey paketin yanındaki `veri` klasöründe tek bir SQLite dosyasında durur;
klasörü silmek uygulamayı tamamen kaldırır.

## Bu paket sunucuya konmaz

İçindeki uygulama Development modunda çalışır: her hedef adrese, `http`'ye ve özel ağ
adreslerine izin verir, böylece denemek için hiçbir ayar gerekmez. Aynı sebeple yalnızca
`localhost`'u dinler ve paylaşılan bir makineye konmamalıdır. Sunucu kurulumu için depo
kökündeki `docker-compose.yml` ve README'nin *Kurulum* bölümü kullanılır; orada hedef alan
adı listesi zorunludur ve yönetici parolası kurulumda belirlenir.

## Paketi yeniden üretmek

Kaynak değiştiğinde paket kendiliğinden güncellenmez. Yeniden üretmek için:

```bash
dotnet publish src/SwaggerDashboard.Web/SwaggerDashboard.Web.csproj \
  -c Release -r win-x64 --self-contained true \
  -o dist/paket/SwaggerDashboard

# .bat ve OKU-BENI.txt zip'in içinden alınır, uygulama klasörünün yanına konur,
# sonra klasörün tamamı sıkıştırılır.
```

`.bat` uygulamayı kendi klasörüne geçtikten sonra başlatır ve bütün ayarları ortam
değişkeniyle verir. İkisi de gereklidir: .NET `appsettings.json`'ı çalışma dizinine göre
arar, başka bir yerden başlatıldığında dosyayı bulamaz ve SQLite yerine SQL Server'a
bağlanmaya çalışıp açılışta çöker.
