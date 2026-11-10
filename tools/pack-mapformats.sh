#!/usr/bin/env bash
# Packs the libraries the game repository restores into the local feed
# bin/nupkg (its MAPTOOLS_DIR/bin/nupkg):
#
#   SourceSharp.MapFormats    the formats: the .nav3d reader the AI navigates by
#   SourceSharp.RoomContracts the mod entity contract MapTools references
#   SourceSharp.MapTools      the compile passes: the game server builds a
#                             map's .nav3d in its own process when it has none
#
# Each version is the <Version> in its project; bump it for every drop handed
# over (the MapFormats csproj says why).
#
#   tools/pack-mapformats.sh              # Release, into bin/nupkg
#   DOTNET=~/.dotnet/dotnet tools/pack-mapformats.sh
#
# After packing it lists each package's files and fails when a package holds
# any assembly but its own, or declares a dependency it should not: the
# formats and the contracts declare none, MapTools exactly the two libraries
# it references and YamlDotNet. Nothing of the CLI, the GPU backend or the
# SQLite cache is packed.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
feed="$root/bin/nupkg"

mkdir -p "$feed"
packages=()
for name in SourceSharp.MapFormats SourceSharp.RoomContracts SourceSharp.MapTools; do
    project="$root/src/$name/$name.csproj"
    "$dotnet" pack "$project" -c Release -o "$feed" --nologo -v q
    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project" | head -n 1)"
    package="$feed/$name.$version.nupkg"
    if [[ ! -f "$package" ]]; then
        echo "pack-mapformats: $package was not written" >&2
        exit 1
    fi
    packages+=("$name=$package")
done

python3 - "${packages[@]}" <<'EOF'
import sys, zipfile, re
allowed = {
    "SourceSharp.MapFormats": set(),
    "SourceSharp.RoomContracts": set(),
    "SourceSharp.MapTools": {"SourceSharp.MapFormats", "SourceSharp.RoomContracts", "YamlDotNet"},
}
for arg in sys.argv[1:]:
    name, path = arg.split("=", 1)
    with zipfile.ZipFile(path) as z:
        dlls = [n for n in z.namelist() if n.lower().endswith(".dll")]
        if dlls != [f"lib/net10.0/{name}.dll"]:
            sys.exit(f"pack-mapformats: {path} holds {dlls}, not only lib/net10.0/{name}.dll")
        nuspec = z.read(f"{name}.nuspec").decode("utf-8")
        deps = set(re.findall(r'<dependency\s+id="([^"]+)"', nuspec))
        if deps != allowed[name]:
            sys.exit(f"pack-mapformats: {path} declares {sorted(deps)}, not {sorted(allowed[name])}")
    print(path)
EOF
