#!/bin/bash
# SessionStart hook для Claude Code on the web (JSON-RPC.NET / WsRpcServer).
#
# Що робить:
#   1) Лише у віддаленому середовищі (CLAUDE_CODE_REMOTE=true) — інакше виходить тихо,
#      щоб не заважати локальній розробці.
#   2) Ставить .NET 10 SDK через apt (за наявності — пропускає). Бібліотека таргетить
#      net10.0; тести теж.
#   3) Виконує `dotnet restore` для тестового проєкту (тягне і референсований
#      src/WsRpcServer). Прогріває NuGet-кеш — усі залежності з nuget.org,
#      приватного feed-у тут НЕМАЄ, тож restore не потребує токена.
#   4) Будує src/WsRpcServer як швидку sanity-перевірку.
#
# Що НЕ робить (свідомо):
#   - НЕ запускає тестовий ран — запускайте явно, щоб бачити вивід:
#     dotnet test tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj
#
# Ідемпотентно: повторні запуски — no-op, якщо SDK уже встановлено і кеш заповнений.

set -euo pipefail

# Локальна розробка має свої dotnet-installs — пропускаємо.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    exit 0
fi

REPO_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
cd "$REPO_DIR"

echo "[session-start] Готую середовище для JSON-RPC.NET у $REPO_DIR"

# 1) .NET 10 SDK (бібліотека таргетить net10.0).
if ! command -v dotnet >/dev/null 2>&1; then
    echo "[session-start] Встановлюю dotnet-sdk-10.0 (apt)..."
    SUDO_PREFIX=""
    if [ "$(id -u)" != "0" ] && command -v sudo >/dev/null 2>&1; then
        SUDO_PREFIX="sudo"
    fi
    $SUDO_PREFIX apt-get update -qq || true
    $SUDO_PREFIX env DEBIAN_FRONTEND=noninteractive \
        apt-get install -y --no-install-recommends dotnet-sdk-10.0
else
    echo "[session-start] dotnet уже встановлено: $(dotnet --version)"
fi

# Telemetry off — детермінованіші логи в сесії.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> "${CLAUDE_ENV_FILE:-/dev/null}" 2>/dev/null || true
echo 'export DOTNET_NOLOGO=1' >> "${CLAUDE_ENV_FILE:-/dev/null}" 2>/dev/null || true

# 2) Прогріваємо NuGet для тестів (тягне і src/WsRpcServer). Усі feed-и публічні.
echo "[session-start] dotnet restore tests/WsRpcServer.Tests..."
dotnet restore tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj --nologo \
    --ignore-failed-sources || true

# 3) Швидка sanity-build бібліотеки.
echo "[session-start] dotnet build src/WsRpcServer (sanity)..."
dotnet build src/WsRpcServer/WsRpcServer.csproj --no-restore --nologo -v minimal || true

echo "[session-start] Готово. Швидкі команди:"
echo "  dotnet build JSON-RPC.NET.sln"
echo "  dotnet test  tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj"
