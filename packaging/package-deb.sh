#!/bin/sh
# Builds the CES .deb for the distribution it runs on (run it inside that distro, e.g. WSL Ubuntu / Debian):
#   packaging/package-deb.sh <version> <repo root>
# Needs publish/linux-x64/gui (GUI, self-contained) and publish/linux-x64/cli (single-file CLI).
# Output: <repo>/release/ces_<version>-1~<distro><release>_amd64.deb
# The package differs per distro in its dependencies (ICU and OpenSSL package names), so build one per target.
set -eu

CES_VERSION="$1"
ROOT="$(cd "$2" && pwd)"
PUB="$ROOT/publish/linux-x64"
OUT="$ROOT/release"
. /etc/os-release
case "$ID" in
  ubuntu) TAG="ubuntu$VERSION_ID" ;;
  debian) TAG="debian$VERSION_ID" ;;
  *) echo "unsupported distribution: $ID" >&2; exit 1 ;;
esac

# ICU of this release (libicu74 on Ubuntu 24.04, libicu76 on Debian 13, ...)
ICU="$(apt-cache search --names-only '^libicu[0-9]+$' | cut -d' ' -f1 | sort -V | tail -n1)"
[ -n "$ICU" ] || { echo "no libicu package found (apt update?)" >&2; exit 1; }
if apt-cache show libssl3t64 >/dev/null 2>&1; then SSL=libssl3t64; else SSL=libssl3; fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DEB="$WORK/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/ces" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/doc/ces"
cp -r "$PUB/gui/." "$DEB/opt/ces/"
cp "$PUB/cli/ces" "$DEB/opt/ces/ces"
find "$DEB/opt/ces" -name '*.pdb' -delete
ln -s /opt/ces/CES "$DEB/usr/bin/ces-gui"
ln -s /opt/ces/ces "$DEB/usr/bin/ces"
cp "$ROOT/packaging/linux/ces.desktop" "$DEB/usr/share/applications/"
for s in 48 128 256 512; do
  mkdir -p "$DEB/usr/share/icons/hicolor/${s}x${s}/apps"
  cp "$ROOT/packaging/linux/ces-$s.png" "$DEB/usr/share/icons/hicolor/${s}x${s}/apps/ces.png"
done
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: CES (Compare Enc Spot)"
  echo
  echo "Files: *"
  echo "License: MIT"
  echo
  cat "$ROOT/LICENSE"
  echo
  cat "$ROOT/THIRD-PARTY-NOTICES.txt"
} > "$DEB/usr/share/doc/ces/copyright"
find "$DEB" -type d -exec chmod 755 {} +
find "$DEB" -type f -exec chmod 644 {} +
chmod 755 "$DEB/opt/ces/CES" "$DEB/opt/ces/ces"
find "$DEB/opt/ces" -name '*.so' -exec chmod 755 {} +

SIZE_KB=$(du -sk --exclude=DEBIAN "$DEB" | cut -f1)
cat > "$DEB/DEBIAN/control" <<EOF
Package: ces
Version: $CES_VERSION-1~$TAG
Architecture: amd64
Maintainer: Taoism <366831+Taoism@users.noreply.github.com>
Installed-Size: $SIZE_KB
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, $ICU, $SSL
Recommends: ffmpeg, mediainfo
Section: video
Priority: optional
Description: Compare Enc Spot - encoder spotting and release comparison
 CES has two modes in one window and one command line (ces):
 .
 Spot: which encoder made each MP3 or FLAC file (LAME tag, Xing/VBRI headers,
 frame-level heuristics; FLAC vendor and compression level), with which
 settings, and whether the stream is intact (frame CRCs, LAME music CRC,
 FLAC MD5 by decoding every frame).
 .
 Compare: Blu-ray, DVD and video releases side by side - frame-exact
 screenshots, audio sync, video and audio analysis, PSNR/SSIM/VMAF,
 remaster grading, subtitle timing. Compare mode needs ffmpeg.
 .
 Both modes read inside RAR sets and ISO images (also an ISO inside a RAR set)
 without unpacking or mounting.
EOF
chmod 644 "$DEB/DEBIAN/control"
mkdir -p "$OUT"
NAME="ces_${CES_VERSION}-1~${TAG}_amd64.deb"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$OUT/$NAME" >/dev/null
echo "built $NAME (depends $ICU, $SSL)"
