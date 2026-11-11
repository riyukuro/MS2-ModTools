#!/usr/bin/env bash
# Runs Gamebryo LightSpeed 3.2 Toolbench + WorldBuilder (the PREBUILT Win32 VC90 "Shipping" build from the SDK, nothing is
# compiled except a 30-line CLR4 shim) headless under Wine. See tools/toolbench/README.md.
#   tools/run-toolbench.sh [file.gsl ...]        # no args: the MangledMetal sample solution (work copy)
#   tools/run-toolbench.sh --setup               # (re)create the working copies, Wine prefix, fonts, shim (idempotent)
#   tools/run-toolbench.sh --shot NAME           # screenshot of the Toolbench display -> <MS2_OUT>/toolbench_shots/NAME.png
#   tools/run-toolbench.sh --xdo ARGS...         # xdotool on the Toolbench display (e.g. --xdo mousemove 11 110 click 1)
#   tools/run-toolbench.sh --stop                # wineserver -k for the Toolbench prefix
# Settings: MS2_GAMEBRYO_APPDIR (your SDK), MS2_OUT (see tools/modtools_config.py).
# Env: TB_DISPLAY (default :98 = a 1920x1200 Xvfb started on demand), MS2_XVFB_CONTAINER (run Xvfb, xdotool and import
#      in this docker container instead of on the host; its /tmp/.X11-unix must be shared with the host),
#      TB_D3D=dxvk (default) | wined3d.
# Toolbench is single-instance (named pipe): a second launch forwards its file args to the running one.
# Needs: wine, winetricks, xvfb, xdotool, ImageMagick, fontconfig, python3 with pefile. Your SDK folder is not written to.
# Layout (all under <MS2_OUT>):
#   toolbench/Shipping      copy of APPDIR/sdk/Win32/Bin/VC90/Shipping + ToolbenchLauncher.exe (CLR4 shim)
#   toolbench/emergent      EMERGENT_PATH: copied Media/ (Toolbench writes compiled .nsb shaders there) + symlinked sdk/ Source/ Documentation/
#   toolbench/samples/MangledMetal/Data   writable copy of the sample game data
#   wineprefix-toolbench    win64 prefix: dotnet48 vcrun2005 vcrun2008 d3dx9_42 d3dx10 d3dx11_42 d3dcompiler_42 dxvk + fonts
set -e
R=$(cd "$(dirname "$0")/.." && pwd)
CFG="python3 $R/tools/modtools_config.py"
O=$($CFG MS2_OUT || echo "$R/out")
T=$O/toolbench
SHOTS=$O/toolbench_shots
TB_DISPLAY=${TB_DISPLAY:-:98}
export WINEPREFIX=$O/wineprefix-toolbench WINEARCH=win64
export DISPLAY=$TB_DISPLAY WAYLAND_DISPLAY=
export WINEDEBUG=${WINEDEBUG:--all}

need_sdk() {
    A=$($CFG MS2_GAMEBRYO_APPDIR) || { echo "MS2_GAMEBRYO_APPDIR is not set: Toolbench comes from your Gamebryo LightSpeed SDK (environment or modtools.toml)" >&2; exit 1; }
    [ -d "$A/sdk/Win32/Bin/VC90/Shipping" ] || { echo "MS2_GAMEBRYO_APPDIR=$A: no sdk/Win32/Bin/VC90/Shipping there" >&2; exit 1; }
}

# Xvfb, xdotool and import run on the host, or in MS2_XVFB_CONTAINER if set.
in_x() {
    if [ -n "${MS2_XVFB_CONTAINER:-}" ]; then docker exec "$MS2_XVFB_CONTAINER" env DISPLAY="$TB_DISPLAY" "$@"; else "$@"; fi
}

ensure_display() {
    local n=${TB_DISPLAY#:}
    [ -z "${MS2_XVFB_CONTAINER:-}" ] || docker start "$MS2_XVFB_CONTAINER" >/dev/null
    if [ ! -S /tmp/.X11-unix/X$n ]; then
        if [ -n "${MS2_XVFB_CONTAINER:-}" ]; then
            docker exec -d "$MS2_XVFB_CONTAINER" Xvfb ":$n" -screen 0 1920x1200x24 -ac +extension GLX +render -noreset
        else
            (nohup Xvfb ":$n" -screen 0 1920x1200x24 -ac +extension GLX +render -noreset >/dev/null 2>&1 &)
        fi
        for _ in $(seq 20); do [ -S /tmp/.X11-unix/X$n ] && break; sleep 0.5; done
    fi
}

setup() {
    need_sdk
    ensure_display
    mkdir -p "$T/samples/MangledMetal" "$T/emergent" "$SHOTS"
    [ -d "$T/Shipping" ] || cp -a "$A/sdk/Win32/Bin/VC90/Shipping" "$T/Shipping"
    [ -d "$T/samples/MangledMetal/Data" ] || cp -a "$A/Samples/GameDemos/MangledMetal/Data" "$T/samples/MangledMetal/"
    [ -d "$T/emergent/Media" ] || cp -a "$A/Media" "$T/emergent/Media"
    for d in sdk Source Documentation; do ln -sfn "$A/$d" "$T/emergent/$d"; done
    [ -d "$WINEPREFIX" ] || wineboot -i
    # dotnet48 is a ~1 h download+install; d3dx* need the DirectX Jun2010 redist (~100 MB)
    winetricks -q --unattended dotnet48 vcrun2005 vcrun2008 d3dx9_42 d3dx10 d3dx11_42 d3dcompiler_42 dxvk
    # Fonts: the prefix's Fonts dir is empty and WPF (DirectWrite) only sees fonts registered under ...\CurrentVersion\Fonts;
    # with no "Arial" it FailFasts in FontFamily.get_FirstFontFamily. Proton's Liberation-based arial/times/cour + wine tahoma.
    local F=$WINEPREFIX/drive_c/windows/Fonts P
    P=$(ls -d "$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/files/share/fonts" 2>/dev/null || true)
    cp -n /usr/share/wine/fonts/*.ttf "$F/"
    cp -n /usr/share/fonts/liberation/*.ttf /usr/share/fonts/TTF/DejaVuSans*.ttf "$F/" 2>/dev/null || true
    [ -n "$P" ] && cp -n "$P"/*.ttf "$F/"
    [ -f "$F/arial.ttf" ] || echo "WARNING: no arial.ttf in $F (run: winetricks corefonts) - WPF will FailFast" >&2
    { echo REGEDIT4; echo
      for k in 'HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Fonts' \
               'HKEY_LOCAL_MACHINE\Software\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Fonts'; do
        echo "[$k]"
        for f in $(ls "$F" | grep -i '\.tt[fc]$'); do
            n=$(fc-scan --format '%{fullname[0]}' "$F/$f" 2>/dev/null); echo "\"${n:-${f%.*}} (TrueType)\"=\"$f\""
        done; echo
      done; } > "$T/fonts.reg"
    wine regedit /S "$(winepath -w "$T/fonts.reg")"
    # CLR4 shim + configs (see tools/toolbench/ToolbenchLauncher.cs)
    cp "$R/tools/toolbench/ToolbenchLauncher.cs" "$T/Shipping/"
    (cd "$T/Shipping" && wine 'C:\windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /platform:x86 \
        /target:winexe '/r:WPF\WindowsBase.dll' /out:ToolbenchLauncher.exe ToolbenchLauncher.cs)
    # The process image decides the address space: mark the shim LARGEADDRESSAWARE like ToolbenchWindowed.exe
    # (Gamebryo's LargeAddressAwarePostBuild.bat); without it a real MS2 map (15k entities) dies with OutOfMemory at 2 GB.
    python3 - "$T/Shipping/ToolbenchLauncher.exe" <<'EOF'
import os, sys, pefile
p = sys.argv[1]; pe = pefile.PE(p)
pe.FILE_HEADER.Characteristics |= 0x20; pe.OPTIONAL_HEADER.CheckSum = pe.generate_checksum()
pe.write(p + ".tmp"); pe.close(); os.replace(p + ".tmp", p)
EOF
    for f in ToolbenchLauncher ToolbenchWindowed ToolbenchConsole; do
        c=$T/Shipping/$f.exe.config
        [ -f "$c" ] && [ ! -f "$c.orig" ] && cp "$c" "$c.orig"
        cat > "$c" <<'EOF'
<?xml version="1.0"?>
<configuration>
  <!-- MS2-ModTools: retargeted from requiredRuntime v2.0.50727 to CLR4 (.NET 4.8 in Wine).
       useLegacyV2RuntimeActivationPolicy lets the VC90 (CLR2) mixed-mode plugins load. -->
  <startup useLegacyV2RuntimeActivationPolicy="true">
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/>
  </startup>
  <runtime>
    <loadFromRemoteSources enabled="true"/>
  </runtime>
</configuration>
EOF
    done
}

case "$1" in
--stop) wineserver -k || true; exit 0 ;;
--shot)
    mkdir -p "$SHOTS"
    if [ -n "${MS2_XVFB_CONTAINER:-}" ]; then
        in_x import -window root /tmp/tb_shot.png
        docker cp -q "$MS2_XVFB_CONTAINER:/tmp/tb_shot.png" "$SHOTS/${2:-shot}.png"
    else
        import -window root "$SHOTS/${2:-shot}.png"
    fi
    echo "$SHOTS/${2:-shot}.png"; exit 0 ;;
--xdo) shift; in_x xdotool "$@"; exit $? ;;
--setup) setup; exit 0 ;;
esac

need_sdk
[ -f "$T/Shipping/ToolbenchLauncher.exe" ] || { echo "run $0 --setup first" >&2; exit 1; }
ensure_display

# Gamebryo runtime DLLs (efd/ecr/egf/Ni*32VC90S.dll, ToolbenchServices32VC90S.dll, shader libs) straight from the SDK.
export WINEPATH="$(winepath -w "$A/sdk/Win32/DLL")"
export EMERGENT_PATH="$(winepath -w "$T/emergent")"
# native d3dx/d3dcompiler (winetricks): Wine's builtins cannot assemble Gamebryo's shaders (same as the client).
ov="d3dx9_42,d3dx10_42,d3dx11_42,d3dcompiler_42=n,b;mshtml="
[ "${TB_D3D:-dxvk}" = wined3d ] && ov="$ov;d3d9,d3d10core,d3d11,dxgi=b"
export WINEDLLOVERRIDES="$ov${WINEDLLOVERRIDES:+;$WINEDLLOVERRIDES}"

args=()
if [ $# -eq 0 ]; then
    args=("$(winepath -w "$T/samples/MangledMetal/Data/MangledMetal.gsl")")
else
    for a in "$@"; do
        if [ -e "$a" ]; then args+=("$(winepath -w "$(realpath "$a")")"); else args+=("$a"); fi
    done
fi
cd "$T/Shipping"
exec wine ToolbenchLauncher.exe "${args[@]}"
