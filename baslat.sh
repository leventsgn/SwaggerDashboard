#!/usr/bin/env bash
#
# Swagger Dashboard'u yerelde tek komutla ayağa kaldırır.
#
# .NET 8 SDK varsa doğrudan çalıştırır. Yoksa Docker'a düşer, çünkü SDK kurmak
# isteyip istemediği kullanıcının kararı; ikisi de yoksa ne indirmesi gerektiğini
# söyler. Betiğin kendi klasörüne geçer, böylece nereden çağrıldığı önemli değil.

set -euo pipefail

cd "$(dirname "$0")"

PORT=5238
URL="http://localhost:${PORT}"
DB="src/SwaggerDashboard.Web/swagger-dashboard.db"

if [ "${1:-}" = "sifirla" ]; then
    echo "Yerel veritabanı siliniyor..."
    rm -f "$DB" "$DB-shm" "$DB-wal"
    echo "Silindi. Uygulama boş bir veritabanıyla açılacak."
    echo
fi

echo "=========================================================="
echo "  Swagger Dashboard - yerel çalıştırma"
echo "=========================================================="
echo

# Tarayıcıyı açan komut işletim sistemine göre değişiyor; hiçbiri yoksa adres
# zaten ekranda yazılı olduğu için sessizce vazgeçilir.
open_browser() {
    sleep 4
    if command -v xdg-open > /dev/null 2>&1; then
        xdg-open "$URL" > /dev/null 2>&1 || true
    elif command -v open > /dev/null 2>&1; then
        open "$URL" > /dev/null 2>&1 || true
    fi
}

has_dotnet8() {
    command -v dotnet > /dev/null 2>&1 || return 1
    dotnet --list-sdks 2> /dev/null | grep -qE '^([89]|[1-9][0-9])\.' || return 1
}

print_credentials() {
    echo "  Adres    : $URL"
    echo "  Kullanıcı: admin"
    echo "  Parola   : development-only-password"
    echo
    echo "Durdurmak için Ctrl+C."
    echo
}

if has_dotnet8; then
    echo ".NET SDK bulundu, uygulama derleniyor..."
    echo
    print_credentials

    # Tarayıcı açılışını launchSettings içindeki "http" profili yapıyor.
    exec dotnet run --project src/SwaggerDashboard.Web --launch-profile http
fi

if command -v docker > /dev/null 2>&1; then
    echo ".NET SDK yok, Docker ile çalıştırılıyor. İlk derleme birkaç dakika sürebilir..."
    echo
    print_credentials

    open_browser &
    exec docker compose -f docker-compose.local.yml up --build
fi

echo ".NET 8 SDK da Docker da bulunamadı."
echo
echo "İkisinden birini kurun:"
echo "  .NET 8 SDK : https://dotnet.microsoft.com/download/dotnet/8.0"
echo "  Docker     : https://www.docker.com/products/docker-desktop/"
exit 1
