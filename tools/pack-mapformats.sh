#!/usr/bin/env bash
# Packs SourceSharp.MapFormats into the local feed bin/nupkg, the way the game
# repository restores it (its MAPTOOLS_DIR/bin/nupkg). The version is the
# <Version> in src/SourceSharp.MapFormats/SourceSharp.MapFormats.csproj; bump
# it for every drop handed over (the csproj says why).
#
#   tools/pack-mapformats.sh              # Release, into bin/nupkg
#   DOTNET=~/.dotnet/dotnet tools/pack-mapformats.sh
#
# After packing it lists the package's files and fails when the package holds
# any assembly but SourceSharp.MapFormats.dll or declares a dependency: the
# game must get the formats and nothing of the compile tools.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
project="$root/src/SourceSharp.MapFormats/SourceSharp.MapFormats.csproj"
feed="$root/bin/nupkg"

mkdir -p "$feed"
"$dotnet" pack "$project" -c Release -o "$feed" --nologo -v q

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project" | head -n 1)"
package="$feed/SourceSharp.MapFormats.$version.nupkg"
if [[ ! -f "$package" ]]; then
    echo "pack-mapformats: $package was not written" >&2
    exit 1
fi

python3 - "$package" <<'EOF'
import sys, zipfile, re
path = sys.argv[1]
with zipfile.ZipFile(path) as z:
    names = z.namelist()
    dlls = [n for n in names if n.lower().endswith(".dll")]
    if dlls != ["lib/net10.0/SourceSharp.MapFormats.dll"]:
        sys.exit(f"pack-mapformats: {path} holds {dlls}, not only lib/net10.0/SourceSharp.MapFormats.dll")
    nuspec = z.read("SourceSharp.MapFormats.nuspec").decode("utf-8")
    if re.search(r"<dependency\b", nuspec):
        sys.exit(f"pack-mapformats: {path} declares a dependency")
print(path)
EOF
