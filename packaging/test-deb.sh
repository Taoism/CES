#!/bin/sh
# Installs a CES .deb (as root, without recommended packages), runs the CLI and GUI briefly, then purges it.
#   sh packaging/test-deb.sh <package.deb>
set -e
DEB="$1"
export DEBIAN_FRONTEND=noninteractive
FIX="$(cd "$(dirname "$0")/../tests/Ces.Tests/Fixtures" && pwd)"
cp "$DEB" /tmp/ces-test.deb
apt-get update -qq >/dev/null 2>&1 || true
apt-get install -y -qq --no-install-recommends /tmp/ces-test.deb >/tmp/ces-install.log 2>&1 || { tail -20 /tmp/ces-install.log; exit 1; }
dpkg -s ces | grep -E "^(Version|Depends)"
ls -la /usr/bin/ces /usr/bin/ces-gui
ces --version
cd "$FIX"
ces spot . rar/set5.part1.rar | tail -3
ces ls rar/set4.rar
ces info ffmpeg-l5.flac 2>&1 | head -2
# GUI under WSLg: start it, check it is still alive after 12 s, then close it
su -c "cd /tmp && (timeout 15 ces-gui $FIX >/tmp/ces-gui.log 2>&1; echo exit \$? >>/tmp/ces-gui.log) &" "$(getent passwd 1000 | cut -d: -f1)"
sleep 12
pgrep -f ces-gui >/dev/null && echo "GUI running" || { echo "GUI not running"; cat /tmp/ces-gui.log; }
sleep 5
tail -3 /tmp/ces-gui.log
apt-get purge -y -qq ces >/dev/null 2>&1
apt-get autoremove -y -qq >/dev/null 2>&1
dpkg -s ces >/dev/null 2>&1 && echo "still installed" || echo "purged"
rm -f /tmp/ces-test.deb
