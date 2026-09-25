#!/bin/bash
# בניית אפליקציית המק (RAF.app) — רץ על מק (גם בבדיקה האוטומטית ב-GitHub).
#   ./mac-app.sh osx-arm64    (שבב של Apple)
#   ./mac-app.sh osx-x64      (Intel)
# הפלט: dist/mac/<rid>/RAF.app ו-dist/mac/RAF-mac-<arm64|intel>.zip
set -euo pipefail
RID="$1"
NAME=$([ "$RID" = "osx-arm64" ] && echo arm64 || echo intel)
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/RAF.Mac/RAF.Mac.csproj)
OUT="dist/mac/$RID"
APP="$OUT/RAF.app"

rm -rf "$OUT"
dotnet publish src/RAF.Mac -c Release -r "$RID" --self-contained -o "$OUT/bin"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/bin/." "$APP/Contents/MacOS/"

# סמל: מהסמל של הממשק, בכל הגדלים שמק מבקש.
ICONSET="$OUT/RAF.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s src/RAF.Shell/Web/icon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) src/RAF.Shell/Web/icon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/RAF.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>RAF.Mac</string>
  <key>CFBundleIdentifier</key><string>com.recoveryadvancedfree.raf</string>
  <key>CFBundleName</key><string>RAF</string>
  <key>CFBundleDisplayName</key><string>שחזור מתקדם חינם</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleIconFile</key><string>RAF</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# חתימה מקומית (ad-hoc): בלי חשבון מפתח של Apple. מק יזהיר בפתיחה הראשונה ("מפתח לא מזוהה").
codesign --force --deep --sign - "$APP"

(cd "$OUT" && ditto -c -k --keepParent RAF.app "../RAF-mac-$NAME.zip")
echo "built: dist/mac/RAF-mac-$NAME.zip"
