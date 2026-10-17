#!/bin/bash
# Compila o Controle da TV (app da barra de menus) e instala em ~/Applications.
# Precisa so das Command Line Tools (swiftc), sem Xcode.
#
#   ./build.sh              compila, assina e instala
#   ./build.sh --so-compilar  compila e assina, sem instalar (mostra onde ficou)
set -euo pipefail

AQUI="$(cd "$(dirname "$0")" && pwd)"
OBRA="${TMPDIR:-/tmp}/controle-tv-mac-build"
APP="$OBRA/Controle da TV.app"
DESTINO="$HOME/Applications/Controle da TV.app"
EXECUTAVEL="ControleDaTV"

rm -rf "$OBRA"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "Compilando..."
/usr/bin/swiftc \
    -O \
    -swift-version 6 \
    -parse-as-library \
    -target arm64-apple-macos14.0 \
    -framework SwiftUI -framework AppKit \
    -o "$APP/Contents/MacOS/$EXECUTAVEL" \
    "$AQUI/App.swift" \
    "$AQUI/Modelo.swift" \
    "$AQUI/CanalRemoto.swift" \
    "$AQUI/Rede.swift" \
    "$AQUI/Painel.swift"

cp "$AQUI/Info.plist" "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "Desenhando o icone..."
"$APP/Contents/MacOS/$EXECUTAVEL" --icone "$OBRA/AppIcon.iconset"
/usr/bin/iconutil -c icns "$OBRA/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"

echo "Assinando..."
/usr/bin/codesign -s - --force --deep "$APP"

if [[ "${1:-}" == "--so-compilar" ]]; then
    echo "Pronto: $APP"
    exit 0
fi

echo "Instalando..."
mkdir -p "$HOME/Applications"
# Fecha a versao antiga se estiver aberta (so este app).
pkill -x "$EXECUTAVEL" 2>/dev/null && sleep 0.5 || true
rm -rf "$DESTINO"
/usr/bin/ditto "$APP" "$DESTINO"
echo "Pronto: $DESTINO"
echo "Para abrir: open \"$DESTINO\""
