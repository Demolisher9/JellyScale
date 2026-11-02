#!/usr/bin/env bash
# Builds JellyScale.exe and JellyScale-Setup.exe on Linux.
# Needs: mono-devel (mcs + .NET 4.8 reference assemblies), binutils-mingw-w64 (windres), nsis (makensis).
# On Windows use tools\build.cmd instead.
set -euo pipefail
cd "$(dirname "$0")/.."
REF=/usr/lib/mono/4.8-api
mkdir -p build

(cd res && x86_64-w64-mingw32-windres --preprocessor=cat -O res -i JellyScale.rc -o JellyScale.res)

mcs -langversion:5 -target:winexe -platform:anycpu -optimize+ -warnaserror+ -nostdlib -noconfig \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Drawing.dll \
  -r:$REF/System.Windows.Forms.dll -r:$REF/System.Management.dll -r:$REF/System.Web.Extensions.dll \
  -r:$REF/System.IO.Compression.dll -r:$REF/System.IO.Compression.FileSystem.dll \
  -win32res:res/JellyScale.res \
  -resource:art/JellyScale.ico,JellyScale.ico -resource:art/JellyScale-128.png,JellyScale-128.png \
  -out:build/JellyScale.exe src/*.cs

if command -v makensis >/dev/null; then
  makensis -V2 -WX Setup.nsi
fi
echo "Built:"; ls -la build/JellyScale.exe JellyScale-Setup.exe 2>/dev/null || true
