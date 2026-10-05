#!/usr/bin/env bash
#
#  Tests the Linux installers - install.sh, uninstall.sh and Install-LiarsBar8P.sh - against
#  fake Steam layouts in a temporary folder. Never touches a real Steam, a real game folder
#  or the network: GitHub is played by a stand-in curl.
#
#      bash tools/test-linux-installers.sh
#
#  Run package.ps1 first: the tests install from dist/LiarsBar-8P.zip, the file players get.
#  Needs bash 4, unzip and Python 3. Runs on Linux, or on Windows in Git Bash - where two of
#  the cases have to allow for Cygwin, which is noted where they do.

set -u
REPO="$(cd "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
[ -f "$REPO/dist/LiarsBar-8P.zip" ] || { echo "No dist/LiarsBar-8P.zip - run package.ps1 first."; exit 2; }
PYTHON=
for p in python3 python; do
    if command -v "$p" > /dev/null 2>&1 && "$p" -c 'import zipfile' > /dev/null 2>&1; then PYTHON="$p"; break; fi
done
[ -n "$PYTHON" ] || { echo "Python 3 is needed for one of the cases."; exit 2; }
WORK="$(mktemp -d "${TMPDIR:-/tmp}/lb8p-installer-tests.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT
# The installers' last-resort look through /run/media, /media and /mnt would otherwise see
# whatever drives the machine running the tests has. Point it at an empty folder instead.
export LB8P_MOUNT_ROOTS="$WORK/no-mounts"
# Nor the tester's own Steam, wherever XDG_DATA_HOME or STEAM_DIR would point the search.
export XDG_DATA_HOME=
unset STEAM_DIR

echo "######## install.sh and uninstall.sh"
BASE="$WORK/installers"; mkdir -p "$BASE"
PASS=0; FAILS=0
ok()   { PASS=$((PASS+1)); echo "  PASS $*"; }
nope() { FAILS=$((FAILS+1)); echo "  FAIL $*"; }
expect_file()    { [ -e "$1" ] && ok "exists: ${1#$BASE/}" || nope "missing: ${1#$BASE/}"; }
expect_no_file() { [ ! -e "$1" ] && ok "gone: ${1#$BASE/}" || nope "still there: ${1#$BASE/}"; }
expect_out()     { grep -qF -- "$2" "$1" && ok "said: $2" || { nope "did not say: $2"; sed 's/^/      | /' "$1"; }; }
expect_not_out() { grep -qF -- "$2" "$1" && { nope "should not say: $2"; sed 's/^/      | /' "$1"; } || ok "did not say: $2"; }

# the release zip, unpacked the way Linux unzip would
PKG="$BASE/pkg"; mkdir -p "$PKG"
unzip -q "$REPO/dist/LiarsBar-8P.zip" -d "$PKG"

make_game() { # $1 = steamapps dir, $2 = installdir name, $3 = write manifest (1/0)
    mkdir -p "$1/common/$2"
    printf 'MZ fake' > "$1/common/$2/Liar's Bar.exe"
    if [ "$3" = 1 ]; then
        printf '"AppState"\n{\n\t"appid"\t\t"3097560"\n\t"name"\t\t"Liar'"'"'s Bar"\n\t"installdir"\t\t"%s"\n}\n' "$2" > "$1/appmanifest_3097560.acf"
    fi
}
vdf_libs() { # $1 = file, rest = library paths
    local f="$1"; shift; local i=0
    { printf '"libraryfolders"\n{\n'
      for p in "$@"; do printf '\t"%d"\n\t{\n\t\t"path"\t\t"%s"\n\t\t"label"\t\t""\n\t\t"apps"\n\t\t{\n\t\t\t"3097560"\t\t"1234"\n\t\t}\n\t}\n' "$i" "$p"; i=$((i+1)); done
      printf '}\n'; } > "$f"
}

echo "== 1. native Steam, game in a second library whose path has spaces"
H1="$BASE/home1"; S1="$H1/.local/share/Steam"; L2="$BASE/media/SD Card/SteamLibrary"
mkdir -p "$S1/steamapps" "$H1/.steam"; ln -s "$S1" "$H1/.steam/steam" 2>/dev/null || true
vdf_libs "$S1/steamapps/libraryfolders.vdf" "$S1" "$L2"
make_game "$L2/steamapps" "Liar's Bar" 1
G1="$L2/steamapps/common/Liar's Bar"
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o1" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o1"; }
expect_out "$BASE/o1" "Game: $G1"
expect_file "$G1/winhttp.dll"; expect_file "$G1/BepInEx/plugins/LiarsBar8P.dll"
expect_file "$G1/BepInEx/core/BepInEx.Core.dll"; expect_file "$G1/dotnet"
expect_file "$G1/doorstop_config.ini"; expect_file "$G1/.doorstop_version"
expect_file "$G1/BepInEx/config/liarsbar.eightplayers.cfg"
expect_no_file "$G1/install.sh"; expect_no_file "$G1/README.md"
expect_out "$BASE/o1" "max 8 players"
expect_out "$BASE/o1" 'WINEDLLOVERRIDES="winhttp=n,b" %command%'
expect_not_out "$BASE/o1" "Steam launch option is already set"

echo "== 2. reinstall over an old copy: stale config + stray plugin go, other mods stay"
printf 'old' > "$G1/BepInEx/config/old.liarsbar.eightplayers.cfg"
printf 'old' > "$G1/BepInEx/plugins/LiarsBar8P-0.9.dll"
printf 'x'   > "$G1/BepInEx/plugins/SomeoneElse.dll"
printf 'MaxPlayers = 6\n' > "$G1/BepInEx/config/liarsbar.eightplayers.cfg"
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o2" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || nope "exit $rc"
expect_no_file "$G1/BepInEx/config/old.liarsbar.eightplayers.cfg"
expect_no_file "$G1/BepInEx/plugins/LiarsBar8P-0.9.dll"
expect_file "$G1/BepInEx/plugins/SomeoneElse.dll"
expect_out "$BASE/o2" "removed old LiarsBar8P-0.9.dll"
expect_out "$BASE/o2" "max 8 players"
grep -q 'MaxPlayers = 8' "$G1/BepInEx/config/liarsbar.eightplayers.cfg" && ok "config replaced" || nope "config not replaced"

echo "== 3. launch option already set for this account"
mkdir -p "$S1/userdata/12345/config"
cat > "$S1/userdata/12345/config/localconfig.vdf" <<'EOF'
"UserLocalConfigStore"
{
	"Software"
	{
		"Valve"
		{
			"Steam"
			{
				"apps"
				{
					"3097560"
					{
						"LastPlayed"		"1"
						"LaunchOptions"		"WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
					}
					"440"
					{
						"LaunchOptions"		"-novid"
					}
				}
			}
		}
	}
}
EOF
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o3" 2>&1
expect_out "$BASE/o3" "Steam launch option is already set"

echo "== 3b. a launch option on ANOTHER game must not count"
sed -i 's/WINEDLLOVERRIDES=\\"winhttp=n,b\\" %command%/-skipintro/; s/"-novid"/"WINEDLLOVERRIDES=\\"winhttp=n,b\\" %command%"/' "$S1/userdata/12345/config/localconfig.vdf"
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o3b" 2>&1
expect_not_out "$BASE/o3b" "Steam launch option is already set"
expect_out "$BASE/o3b" "ONE MORE STEP"

echo "== 3c. launch option variants: only the working form counts"
set_lo() { # $1 = the launch option as typed into Steam; written as Steam stores it
    local v="${1//\"/\\\"}"
    printf '"UserLocalConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"apps"\n\t\t\t\t{\n\t\t\t\t\t"3097560"\n\t\t\t\t\t{\n\t\t\t\t\t\t"LaunchOptions"\t\t"%s"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n' "$v" \
        > "$S1/userdata/12345/config/localconfig.vdf"
}
lo_case() { # $1 = launch option, $2 = expected: set | wrong | unset
    set_lo "$1"
    HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o3c" 2>&1
    case "$2" in
        set)   grep -qF "Steam launch option is already set" "$BASE/o3c" && ok "set:   $1" || nope "should count as set: $1" ;;
        wrong) grep -qF "not quite" "$BASE/o3c" && grep -qF "ONE MORE STEP" "$BASE/o3c" && ok "wrong: $1" || nope "should be called wrong: $1" ;;
        unset) ! grep -qF "not quite" "$BASE/o3c" && grep -qF "ONE MORE STEP" "$BASE/o3c" && ok "unset: $1" || nope "should be plain unset: $1" ;;
    esac
}
lo_case 'WINEDLLOVERRIDES="winhttp=n,b" %command%' set
lo_case 'WINEDLLOVERRIDES="winhttp.dll=n,b" %command%' set
lo_case 'WINEDLLOVERRIDES="dinput8=n,b;winhttp=native,builtin" gamemoderun %command%' set
lo_case 'WINEDLLOVERRIDES="winhttp=n" %command%' wrong
lo_case 'WINEDLLOVERRIDES="winhttp=n,b"' wrong
lo_case 'winhttp=n,b %command%' wrong
lo_case '-skipintro' unset
echo "== 4. Flatpak Steam only, manifest present"
H4="$BASE/home4"; S4="$H4/.var/app/com.valvesoftware.Steam/.local/share/Steam"
mkdir -p "$S4/steamapps"; vdf_libs "$S4/steamapps/libraryfolders.vdf" "$S4"
make_game "$S4/steamapps" "Liar's Bar" 1
HOME="$H4" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o4" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o4"; }
expect_file "$S4/steamapps/common/Liar's Bar/BepInEx/plugins/LiarsBar8P.dll"

echo "== 5. no manifest: falls back to the conventional folder name"
H5="$BASE/home5"; S5="$H5/.steam/debian-installation"
mkdir -p "$S5/steamapps"; make_game "$S5/steamapps" "Liar's Bar" 0
HOME="$H5" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o5" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o5"; }

echo "== 6. no Steam anywhere: asks, accepts a quoted path with a trailing slash"
H6="$BASE/home6"; mkdir -p "$H6"; G6="$BASE/elsewhere/Liar's Bar"
mkdir -p "$G6"; printf 'MZ' > "$G6/Liar's Bar.exe"
printf '%s\n' "'$BASE/elsewhere/Liar'\\''s Bar/'" | HOME="$H6" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o6" 2>&1 < /dev/stdin; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o6"; }
expect_file "$G6/BepInEx/plugins/LiarsBar8P.dll"

echo "== 6b. no Steam, Enter pressed: cancels, changes nothing"
H6b="$BASE/home6b"; mkdir -p "$H6b"
printf '\n' | HOME="$H6b" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o6b" 2>&1; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0 on cancel"
expect_out "$BASE/o6b" "Cancelled."

echo "== 7. path given as an argument, with a ~ and quotes"
H7="$BASE/home7"; mkdir -p "$H7/games/Liar's Bar"; printf 'MZ' > "$H7/games/Liar's Bar/Liar's Bar.exe"
HOME="$H7" bash "$PKG/install.sh" "\"~/games/Liar's Bar/\"" > "$BASE/o7" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o7"; }
expect_file "$H7/games/Liar's Bar/winhttp.dll"

echo "== 8. a wrong folder given: refuses, changes nothing"
mkdir -p "$BASE/notgame"
HOME="$H7" bash "$PKG/install.sh" "$BASE/notgame" > "$BASE/o8" 2>&1; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_no_file "$BASE/notgame/BepInEx"

echo "== 9. zip extracted straight into the game folder"
G9="$BASE/home9/Liar's Bar"; mkdir -p "$G9"; printf 'MZ' > "$G9/Liar's Bar.exe"
cp -R "$PKG"/. "$G9/"
HOME="$BASE/home9" bash "$G9/install.sh" "$G9" > "$BASE/o9" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o9"; }
expect_out "$BASE/o9" "already in the game folder"
expect_file "$G9/BepInEx/plugins/LiarsBar8P.dll"

echo "== 10. game running (seen by its .exe): refuses"
# Cygwin strips one ".exe" from argv[0] in /proc; Linux and Wine keep it. Hence two here.
bash -c 'exec -a "Z:\\games\\Liar'"'"'s Bar\\Liar'"'"'s Bar.exe.exe" sleep 20' & SPID=$!
sleep 1
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o10" 2>&1; rc=$?
kill $SPID 2>/dev/null
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0 while running"
expect_out "$BASE/o10" "Liar's Bar is running"

echo "== 10b. game running (seen by Steam's launcher): refuses"
bash -c 'sleep 20; :' reaper SteamLaunch AppId=3097560 -- /x/proton waitforexitandrun & SPID=$!
sleep 1
HOME="$H1" XDG_DATA_HOME= bash "$PKG/uninstall.sh" > "$BASE/o10b" 2>&1; rc=$?
kill $SPID 2>/dev/null
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0 while running"
expect_out "$BASE/o10b" "Liar's Bar is running"

echo "== 10c. a different game's launcher does not count"
bash -c 'sleep 20; :' reaper SteamLaunch AppId=30975601 -- x & SPID=$!
sleep 1
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o10c" 2>&1; rc=$?
kill $SPID 2>/dev/null
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o10c"; }

echo "== 11. installer run from a folder without its files"
mkdir -p "$BASE/lonely"; cp "$PKG/install.sh" "$BASE/lonely/"
HOME="$H1" XDG_DATA_HOME= bash "$BASE/lonely/install.sh" > "$BASE/o11" 2>&1; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_out "$BASE/o11" "missing its files"

echo "== 12. uninstall with another mod present: only this mod goes"
HOME="$H1" XDG_DATA_HOME= bash "$PKG/uninstall.sh" > "$BASE/o12" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o12"; }
expect_no_file "$G1/BepInEx/plugins/LiarsBar8P.dll"
expect_no_file "$G1/BepInEx/config/liarsbar.eightplayers.cfg"
expect_file "$G1/BepInEx/plugins/SomeoneElse.dll"; expect_file "$G1/winhttp.dll"
expect_out "$BASE/o12" "SomeoneElse.dll"

echo "== 13. uninstall with nothing else: back to vanilla, game untouched"
rm -f "$G1/BepInEx/plugins/SomeoneElse.dll"
HOME="$H1" XDG_DATA_HOME= bash "$PKG/install.sh" > /dev/null 2>&1
HOME="$H1" XDG_DATA_HOME= bash "$PKG/uninstall.sh" > "$BASE/o13" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || nope "exit $rc"
for t in BepInEx dotnet winhttp.dll doorstop_config.ini .doorstop_version changelog.txt; do expect_no_file "$G1/$t"; done
expect_file "$G1/Liar's Bar.exe"
expect_out "$BASE/o13" "back to vanilla"
left="$(ls -A "$G1")"; [ "$left" = "Liar's Bar.exe" ] && ok "only the game is left" || nope "left behind: $left"

echo "== 14. uninstall again: nothing to remove"
HOME="$H1" XDG_DATA_HOME= bash "$PKG/uninstall.sh" > "$BASE/o14" 2>&1
expect_out "$BASE/o14" "Nothing to remove"

echo "== 15. uninstall pointed at a non-game folder: refuses, deletes nothing"
mkdir -p "$BASE/precious/BepInEx"; printf 'keep' > "$BASE/precious/winhttp.dll"
HOME="$H1" bash "$PKG/uninstall.sh" "$BASE/precious" > "$BASE/o15" 2>&1; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_file "$BASE/precious/winhttp.dll"; expect_file "$BASE/precious/BepInEx"

echo "== 16. installed under two Steams (native + Flatpak): asks which"
H16="$BASE/home16"; N16="$H16/.local/share/Steam"; F16="$H16/.var/app/com.valvesoftware.Steam/.local/share/Steam"
mkdir -p "$N16/steamapps" "$F16/steamapps"
make_game "$N16/steamapps" "Liar's Bar" 1; make_game "$F16/steamapps" "Liar's Bar" 1
printf '2\n' | HOME="$H16" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o16" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o16"; }
expect_out "$BASE/o16" "installed in more than one place"
expect_out "$BASE/o16" "(Flatpak Steam)"
n_native=$([ -e "$N16/steamapps/common/Liar's Bar/winhttp.dll" ] && echo 1 || echo 0)
n_flat=$([ -e "$F16/steamapps/common/Liar's Bar/winhttp.dll" ] && echo 1 || echo 0)
[ "$n_native$n_flat" = "01" ] || [ "$n_native$n_flat" = "10" ] && ok "installed into exactly the one chosen" || nope "native=$n_native flatpak=$n_flat"
chosen="$(sed -n 's/^     2) \(.*\)   (.*$/\1/p' "$BASE/o16")"
[ -e "$chosen/winhttp.dll" ] && ok "and it was number 2" || nope "number 2 ($chosen) was not the one installed"

echo "== 16b. two installs, Enter pressed: cancels, changes nothing"
rm -rf "$N16/steamapps/common/Liar's Bar/winhttp.dll" "$F16/steamapps/common/Liar's Bar/winhttp.dll"
printf '\n' | HOME="$H16" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o16b" 2>&1; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_no_file "$N16/steamapps/common/Liar's Bar/winhttp.dll"; expect_no_file "$F16/steamapps/common/Liar's Bar/winhttp.dll"

echo "== 16c. two installs, a number that is not on the list: refuses"
printf '7\n' | HOME="$H16" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o16c" 2>&1; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_out "$BASE/o16c" "There is no number 7"

echo "== 17. SD card listed but not inserted, game nowhere: says so"
H17="$BASE/home17"; S17="$H17/.local/share/Steam"; mkdir -p "$S17/steamapps"
vdf_libs "$S17/steamapps/libraryfolders.vdf" "$S17" "/run/media/deck/Not-Inserted-Card"
printf '\n' | HOME="$H17" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o17" 2>&1
expect_out "$BASE/o17" "Steam lists a library at /run/media/deck/Not-Inserted-Card, but it is not there"

echo "== 18. STEAM_DIR points at a Steam nowhere usual"
H18="$BASE/home18"; mkdir -p "$H18"; S18="$BASE/opt/odd steam"; mkdir -p "$S18/steamapps"
make_game "$S18/steamapps" "Liar's Bar" 1
HOME="$H18" STEAM_DIR="$S18" bash "$PKG/install.sh" > "$BASE/o18" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o18"; }
expect_file "$S18/steamapps/common/Liar's Bar/winhttp.dll"

echo "== 19. Flatpak's vdf names its own root as ~/.local/share/Steam, no native Steam"
H19="$BASE/home19"; F19="$H19/.var/app/com.valvesoftware.Steam/.local/share/Steam"; mkdir -p "$F19/steamapps"
vdf_libs "$F19/steamapps/libraryfolders.vdf" "$H19/.local/share/Steam"
make_game "$F19/steamapps" "Liar's Bar" 1
printf '\n' | HOME="$H19" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o19" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o19"; }
expect_not_out "$BASE/o19" "but it is not there"
expect_file "$F19/steamapps/common/Liar's Bar/winhttp.dll"

echo "== 20. old SteamApps capitalisation and pre-2021 numbered library list"
H20="$BASE/home20"; S20="$H20/.local/share/Steam"; L20="$BASE/old lib"; mkdir -p "$S20/SteamApps" "$L20/SteamApps"
printf '"LibraryFolders"\n{\n\t"TimeNextStatsReport"\t\t"1"\n\t"1"\t\t"%s"\n}\n' "$L20" > "$S20/SteamApps/libraryfolders.vdf"
make_game "$L20/SteamApps" "Liar's Bar" 1
HOME="$H20" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o20" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o20"; }
expect_file "$L20/SteamApps/common/Liar's Bar/winhttp.dll"

echo "== 21. another Liar's Bar mod is left alone; this mod's stray copy does not keep BepInEx"
G21="$L20/SteamApps/common/Liar's Bar"
printf 'x' > "$G21/BepInEx/plugins/LiarsBarEnhance.dll"
printf 'x' > "$G21/BepInEx/plugins/liarsbar8p.dll.old"
HOME="$H20" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o21" 2>&1
expect_file "$G21/BepInEx/plugins/LiarsBarEnhance.dll"
expect_no_file "$G21/BepInEx/plugins/liarsbar8p.dll.old"
rm -f "$G21/BepInEx/plugins/LiarsBarEnhance.dll"; printf 'x' > "$G21/BepInEx/plugins/LiarsBar8P-copy.dll"
HOME="$H20" XDG_DATA_HOME= bash "$PKG/uninstall.sh" > "$BASE/o21u" 2>&1
expect_out "$BASE/o21u" "back to vanilla"
expect_no_file "$G21/BepInEx"

echo "== 22. the shared block is identical in all three scripts"
sums="$(for f in install.sh uninstall.sh Install-LiarsBar8P.sh; do sed -n '/^# >>> finding the game/,/^# <<< finding the game/p' "$REPO/installer/$f" | md5sum; done | sort -u | wc -l)"
[ "$sums" -eq 1 ] && ok "one version of the block" || nope "$sums different versions of the block"

echo "== 23. Steam's list still has the SD card's old mount point; the card is mounted elsewhere"
H23="$BASE/home23"; S23="$H23/.local/share/Steam"; mkdir -p "$S23/steamapps"
vdf_libs "$S23/steamapps/libraryfolders.vdf" "$S23" "/run/media/mmcblk0p1"
M23="$BASE/mounts23"; make_game "$M23/deck/SDCARD/steamapps" "Liar's Bar" 1
HOME="$H23" XDG_DATA_HOME= LB8P_MOUNT_ROOTS="$M23" bash "$PKG/install.sh" > "$BASE/o23" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o23"; }
expect_file "$M23/deck/SDCARD/steamapps/common/Liar's Bar/winhttp.dll"
expect_not_out "$BASE/o23" "but it is not there"
mklink() { MSYS=winsymlinks:lnk ln -s "$1" "$2" 2> /dev/null && [ -L "$2" ]; }
fresh_game() { # $1 = folder: an installed game with nothing else in it
    mkdir -p "$1"; printf 'MZ' > "$1/Liar's Bar.exe"
    bash "$PKG/install.sh" "$1" > /dev/null 2>&1
}

echo "== 24. uninstall: another mod switched off, a patcher, or a linked plugin each keep BepInEx"
for kind in disabled patcher link; do
    G24="$BASE/g24-$kind/Liar's Bar"; fresh_game "$G24"
    case "$kind" in
        disabled) printf 'x' > "$G24/BepInEx/plugins/OtherMod.dll.disabled" ;;
        patcher)  printf 'x' > "$G24/BepInEx/patchers/OtherPatcher.dll" ;;
        link)     printf 'x' > "$BASE/elsewhere-mod.dll"
                  mklink "$BASE/elsewhere-mod.dll" "$G24/BepInEx/plugins/Linked.dll" \
                      || { echo "  SKIP symlinks are not available here"; continue; } ;;
    esac
    HOME="$BASE/home24" bash "$PKG/uninstall.sh" "$G24" > "$BASE/o24" 2>&1
    expect_file "$G24/winhttp.dll"
    expect_no_file "$G24/BepInEx/plugins/LiarsBar8P.dll"
    expect_out "$BASE/o24" "Leaving BepInEx in place"
done
[ -e "$BASE/elsewhere-mod.dll" ] && ok "the linked mod's own file is untouched" || nope "the linked mod's file is gone"

echo "== 24b. uninstall: a copy of this mod in a subfolder or as a link goes too, and so does BepInEx"
G24b="$BASE/g24b/Liar's Bar"; fresh_game "$G24b"
mkdir -p "$G24b/BepInEx/plugins/dracbat-LiarsBar8P"; printf 'x' > "$G24b/BepInEx/plugins/dracbat-LiarsBar8P/LiarsBar8P.dll"
HOME="$BASE/home24b" bash "$PKG/uninstall.sh" "$G24b" > "$BASE/o24b" 2>&1
expect_no_file "$G24b/BepInEx/plugins/dracbat-LiarsBar8P/LiarsBar8P.dll"
expect_no_file "$G24b/winhttp.dll"
expect_out "$BASE/o24b" "back to vanilla"
G24c="$BASE/g24c/Liar's Bar"; fresh_game "$G24c"
printf 'MY OWN BUILD' > "$BASE/dev-build-24c.dll"; rm -f "$G24c/BepInEx/plugins/LiarsBar8P.dll"
if mklink "$BASE/dev-build-24c.dll" "$G24c/BepInEx/plugins/LiarsBar8P.dll"; then
    HOME="$BASE/home24c" bash "$PKG/uninstall.sh" "$G24c" > "$BASE/o24c" 2>&1
    expect_no_file "$G24c/BepInEx/plugins/LiarsBar8P.dll"
    expect_no_file "$G24c/winhttp.dll"
    [ "$(cat "$BASE/dev-build-24c.dll")" = "MY OWN BUILD" ] && ok "the linked build itself is untouched" || nope "the linked build was changed"
else
    echo "  SKIP symlinks are not available here"
fi
echo "== 24d. install: a stray copy of this mod in a subfolder is cleared, so it cannot load twice"
G24d="$BASE/g24d/Liar's Bar"; fresh_game "$G24d"
mkdir -p "$G24d/BepInEx/plugins/old"; printf 'x' > "$G24d/BepInEx/plugins/old/LiarsBar8P.dll"
printf 'x' > "$G24d/BepInEx/plugins/old/SomeoneElse.dll"
HOME="$BASE/home24d" bash "$PKG/install.sh" "$G24d" > "$BASE/o24d" 2>&1
expect_no_file "$G24d/BepInEx/plugins/old/LiarsBar8P.dll"
expect_file "$G24d/BepInEx/plugins/old/SomeoneElse.dll"
expect_file "$G24d/BepInEx/plugins/LiarsBar8P.dll"

echo "== 25. uninstall with no other mod but another mod's settings: loader goes, settings stay"
G25="$BASE/g25/Liar's Bar"; fresh_game "$G25"
printf 'keep me' > "$G25/BepInEx/config/com.someone.othermod.cfg"
printf '[x]' > "$G25/BepInEx/config/BepInEx.cfg"
HOME="$BASE/home25" bash "$PKG/uninstall.sh" "$G25" > "$BASE/o25" 2>&1
expect_file "$G25/BepInEx/config/com.someone.othermod.cfg"
expect_no_file "$G25/BepInEx/config/BepInEx.cfg"; expect_no_file "$G25/BepInEx/core"
expect_no_file "$G25/winhttp.dll"; expect_no_file "$G25/dotnet"
expect_out "$BASE/o25" "keeping other mods' settings"

echo "== 26. launch option: lists, a repeated variable, other Steams and other accounts"
lo_case 'WINEDLLOVERRIDES="winhttp,dxgi=n,b" %command%' set
lo_case 'WINEDLLOVERRIDES="winhttp=n,b" WINEDLLOVERRIDES="dxgi=n,b" %command%' wrong
lo_case '%command% WINEDLLOVERRIDES="winhttp=n,b"' wrong
lo_case 'gamemoderun %command% -skipintro WINEDLLOVERRIDES="winhttp=n,b"' wrong
lo_case "WINEDLLOVERRIDES='winhttp=n,b' %command%" set
lo_case 'WINEDLLOVERRIDES=winhttp=n,b %command%' set
write_lo() { # $1 = localconfig.vdf, $2 = launch option ("" = a Liar's Bar entry with none)
    mkdir -p "$(dirname "$1")"
    local v="${2//\"/\\\"}"
    printf '"UserLocalConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"apps"\n\t\t\t\t{\n\t\t\t\t\t"3097560"\n\t\t\t\t\t{\n\t\t\t\t\t\t"LaunchOptions"\t\t"%s"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n' "$v" > "$1"
}
H26="$BASE/home26"; N26="$H26/.local/share/Steam"; F26="$H26/.var/app/com.valvesoftware.Steam/.local/share/Steam"
mkdir -p "$N26/steamapps" "$F26/steamapps"; make_game "$N26/steamapps" "Liar's Bar" 1
write_lo "$N26/userdata/111/config/localconfig.vdf" ""
write_lo "$F26/userdata/222/config/localconfig.vdf" 'WINEDLLOVERRIDES="winhttp=n,b" %command%'
HOME="$H26" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o26" 2>&1
expect_not_out "$BASE/o26" "already set"
expect_out "$BASE/o26" "ONE MORE STEP"
write_lo "$N26/userdata/333/config/localconfig.vdf" 'WINEDLLOVERRIDES="winhttp=n,b" %command%'
HOME="$H26" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o26b" 2>&1
expect_out "$BASE/o26b" "set for one Steam account on this machine but not another"

echo "== 27. a pasted file:// link and a tab-completed path both work"
H27="$BASE/home27"; G27="$BASE/g27/Program Files (x86)/Liar's Bar"; mkdir -p "$H27" "$G27"; printf 'MZ' > "$G27/Liar's Bar.exe"
url="file://$(printf '%s' "$G27" | sed "s/ /%20/g; s/'/%27/g")"
printf '%s\n' "$url" | HOME="$H27" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o27" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "file:// link accepted" || { nope "file:// link: exit $rc"; sed 's/^/      | /' "$BASE/o27"; }
escaped="$(printf '%s' "$G27" | sed 's/[][ ()'"'"']/\\&/g')"
HOME="$H27" bash "$PKG/uninstall.sh" "$escaped" > "$BASE/o27b" 2>&1; rc=$?
[ $rc -eq 0 ] && ok "backslash-escaped path accepted" || { nope "escaped path: exit $rc"; sed 's/^/      | /' "$BASE/o27b"; }

echo "== 28. a plugin linked to a file outside the game is replaced, not written through"
G28="$BASE/g28/Liar's Bar"; fresh_game "$G28"
printf 'MY OWN BUILD' > "$BASE/my-build.dll"
rm -f "$G28/BepInEx/plugins/LiarsBar8P.dll"
if mklink "$BASE/my-build.dll" "$G28/BepInEx/plugins/LiarsBar8P.dll"; then
    HOME="$BASE/home28" bash "$PKG/install.sh" "$G28" > "$BASE/o28" 2>&1
    [ "$(cat "$BASE/my-build.dll")" = "MY OWN BUILD" ] && ok "the file outside the game is untouched" || nope "the file outside the game was overwritten"
    [ ! -L "$G28/BepInEx/plugins/LiarsBar8P.dll" ] && [ -s "$G28/BepInEx/plugins/LiarsBar8P.dll" ] \
        && ok "the link was replaced by the real plugin" || nope "the plugin is still a link, or missing"
else
    echo "  SKIP symlinks are not available here"
fi

echo "== 29. Snap, hidden-folder Snap and the Flatpak's data/Steam layout"
n29=0
for layout in "snap/steam/common/.local/share/Steam" ".snap/data/steam/common/.local/share/Steam" \
              ".var/app/com.valvesoftware.Steam/data/Steam"; do
    n29=$((n29 + 1)); H29="$BASE/home29-$n29"; S29="$H29/$layout"; L29="$BASE/lib 29-$n29"
    mkdir -p "$S29/steamapps"; vdf_libs "$S29/steamapps/libraryfolders.vdf" "$S29" "$L29"
    make_game "$L29/steamapps" "Liar's Bar" 1
    HOME="$H29" XDG_DATA_HOME= bash "$PKG/install.sh" > "$BASE/o29" 2>&1; rc=$?
    if [ $rc -eq 0 ] && [ -e "$L29/steamapps/common/Liar's Bar/winhttp.dll" ]; then ok "~/$layout"
    else nope "~/$layout: exit $rc"; sed 's/^/      | /' "$BASE/o29"; fi
done
echo
echo "######## Install-LiarsBar8P.sh"
BASE="$WORK/online"; mkdir -p "$BASE/bin"
ok()   { PASS=$((PASS+1)); echo "  PASS $*"; }
nope() { FAILS=$((FAILS+1)); echo "  FAIL $*"; }
expect_out()     { grep -qF -- "$2" "$1" && ok "said: $2" || { nope "did not say: $2"; sed 's/^/      | /' "$1"; }; }
expect_file()    { [ -e "$1" ] && ok "exists: ${1##*/}" || nope "missing: $1"; }
expect_no_file() { [ ! -e "$1" ] && ok "absent: ${1##*/}" || nope "should not exist: $1"; }

# Stand-in curl: the API URL gets $FIXTURE (or a 404), any other URL gets $ZIPFILE.
# Every URL asked for is logged, so a test can prove nothing was fetched.
cat > "$BASE/bin/curl" <<'EOF'
#!/usr/bin/env bash
out=; fmt=; url=
while [ $# -gt 0 ]; do
    case "$1" in
        -o) out="$2"; shift 2 ;;
        -w) fmt="$2"; shift 2 ;;
        -H|--proto|--proto-redir) shift 2 ;;
        -*) shift ;;
        *) url="$1"; shift ;;
    esac
done
echo "$url" >> "$CURL_LOG"
case "$url" in
    https://api.github.com/*)
        if [ "$FIXTURE" = 404 ]; then printf '{"message":"Not Found"}' > "$out"; [ -n "$fmt" ] && printf 404; exit 0; fi
        cp "$FIXTURE" "$out"; [ -n "$fmt" ] && printf 200; exit 0 ;;
    *)  cp "$ZIPFILE" "$out"; [ -n "$fmt" ] && printf 200; exit 0 ;;
esac
EOF
chmod +x "$BASE/bin/curl"

# A Steam with the game in it, as in run.sh case 1
H="$BASE/home"; S="$H/.local/share/Steam"; G="$S/steamapps/common/Liar's Bar"
mkdir -p "$G"; printf 'MZ' > "$G/Liar's Bar.exe"
printf '"AppState"\n{\n\t"appid"\t\t"3097560"\n\t"installdir"\t\t"Liar'"'"'s Bar"\n}\n' > "$S/steamapps/appmanifest_3097560.acf"
printf 'previous' > "$BASE/prev.dll"

release_json() { # $1 = zip url, $2 = pretty(1)/minified(0), $3 = tag (default v1.1.0)
    local tag="${3:-v1.1.0}"
    local body='Fixes. Do not trust \"browser_download_url\": \"https://evil.example/x.zip\" in text.'
    if [ "$2" = 1 ]; then
        cat <<EOF
{
  "url": "https://api.github.com/repos/dracbat/liarsbar-8p/releases/1",
  "tag_name": "$tag",
  "name": "Liar's Bar 8 Player Mod v1.1.0",
  "assets": [
    {
      "name": "Install-LiarsBar8P.sh",
      "uploader": { "login": "dracbat", "type": "User" },
      "browser_download_url": "https://github.com/dracbat/liarsbar-8p/releases/download/v1.1.0/Install-LiarsBar8P.sh"
    },
    {
      "name": "LiarsBar-8P.zip",
      "size": 34263910,
      "browser_download_url": "$1"
    }
  ],
  "body": "$body"
}
EOF
    else
        printf '{"url":"x","tag_name":"'"$tag"'","assets":[{"name":"Install-LiarsBar8P.bat","browser_download_url":"https://github.com/dracbat/liarsbar-8p/releases/download/v1.1.0/Install-LiarsBar8P.bat"},{"name":"LiarsBar-8P.zip","browser_download_url":"%s"}],"body":"%s"}' "$1" "$body"
    fi
}

run_online() { # $1 = name; env FIXTURE / ZIPFILE set by caller
    : > "$BASE/curl-$1.log"
    PATH="$BASE/bin:$PATH" CURL_LOG="$BASE/curl-$1.log" HOME="$H" XDG_DATA_HOME= \
        bash "$REPO/installer/Install-LiarsBar8P.sh" > "$BASE/o-$1" 2>&1
}
reset_game() { rm -rf "$G/BepInEx" "$G/dotnet" "$G/winhttp.dll" "$G/doorstop_config.ini" "$G/.doorstop_version" "$G/changelog.txt"
               mkdir -p "$G/BepInEx/plugins"; cp "$BASE/prev.dll" "$G/BepInEx/plugins/LiarsBar8P.dll"; }
GOOD="https://github.com/dracbat/liarsbar-8p/releases/download/v1.1.0/LiarsBar-8P.zip"
export ZIPFILE="$REPO/dist/LiarsBar-8P.zip"

for pretty in 1 0; do
    echo "== good release ($([ $pretty = 1 ] && echo pretty || echo minified) JSON)"
    reset_game
    release_json "$GOOD" $pretty > "$BASE/rel.json"; export FIXTURE="$BASE/rel.json"
    run_online "good$pretty"; rc=$?
    [ $rc -eq 0 ] && ok "exit 0" || { nope "exit $rc"; sed 's/^/      | /' "$BASE/o-good$pretty"; }
    grep -qxF "$GOOD" "$BASE/curl-good$pretty.log" && ok "downloaded the zip from GitHub" || nope "zip not fetched"
    grep -q evil "$BASE/curl-good$pretty.log" && nope "fetched the URL quoted in the release notes" || ok "ignored the URL in the release notes"
    expect_out "$BASE/o-good$pretty" "Version v1.1.0"
    expect_out "$BASE/o-good$pretty" "Installed successfully - max 8 players"
    expect_out "$BASE/o-good$pretty" "must read v1.1.0"
    expect_not_out "$BASE/o-good$pretty" "To remove: bash uninstall.sh"
    expect_out "$BASE/o-good$pretty" "To remove the mod"
    cmp -s "$G/BepInEx/plugins/LiarsBar8P.dll" "$BASE/prev.dll" && nope "previous plugin still in place" || ok "previous plugin replaced"
    expect_file "$G/winhttp.dll"
done
leftover="$(ls -d "${TMPDIR:-/tmp}"/LiarsBar8P.* 2>/dev/null)"
[ -z "$leftover" ] && ok "temporary folder cleaned up" || nope "left behind: $leftover"

for evil in "https://evil.example.com/LiarsBar-8P.zip" "https://github.com.evil.example/LiarsBar-8P.zip" \
            "https://github.com@evil.example/LiarsBar-8P.zip" "http://github.com/dracbat/liarsbar-8p/LiarsBar-8P.zip" \
            "https://github.com/someone-else/anything/releases/download/v1.1.0/LiarsBar-8P.zip" \
            "https://objects.githubusercontent.com/anything/LiarsBar-8P.zip" \
            "https://release-assets.githubusercontent.com/anything/LiarsBar-8P.zip" \
            "https://github.com//evil.example/LiarsBar-8P.zip" \
            "https://github.com/dracbat/liarsbar-8p/releases/download/v1.1.0/../../../x/LiarsBar-8P.zip" \
            "https://github.com/dracbat/liarsbar-8p/releases/download/v0.1.0/LiarsBar-8P.zip"; do
    echo "== download link $evil: refused, nothing fetched, nothing changed"
    reset_game
    release_json "$evil" 1 > "$BASE/rel.json"; export FIXTURE="$BASE/rel.json"
    run_online evil; rc=$?
    [ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
    expect_out "$BASE/o-evil" "not one of this project's own releases"
    [ "$(wc -l < "$BASE/curl-evil.log")" -eq 1 ] && ok "only the API was asked" || { nope "fetched more:"; cat "$BASE/curl-evil.log"; }
    cmp -s "$G/BepInEx/plugins/LiarsBar8P.dll" "$BASE/prev.dll" && ok "previous plugin untouched" || nope "previous plugin changed"
done

echo "== a tag with terminal control characters in it is never printed"
reset_game
printf '{"tag_name":"v1.1.0\033]0;pwned\007\033[2J","assets":[{"browser_download_url":"%s"}]}' "$GOOD" > "$BASE/rel.json"
export FIXTURE="$BASE/rel.json"
run_online esc; rc=$?
grep -q "$(printf '\033')" "$BASE/o-esc" && nope "an escape sequence from the tag reached the terminal" || ok "no escape sequence printed"
expect_out "$BASE/o-esc" "Version unknown"

echo "== no release published (404)"
reset_game; export FIXTURE=404
run_online nf; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_out "$BASE/o-nf" "No published release found"
cmp -s "$G/BepInEx/plugins/LiarsBar8P.dll" "$BASE/prev.dll" && ok "previous plugin untouched" || nope "previous plugin changed"

echo "== a release from before Linux support (no install.sh in the zip)"
reset_game
"$PYTHON" - "$REPO/dist/LiarsBar-8P.zip" "$BASE/old.zip" <<'PY'
import sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(src) as a, zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as b:
    for i in a.infolist():
        if not i.filename.endswith(".sh"):
            b.writestr(i, a.read(i))
PY
release_json "$GOOD" 1 > "$BASE/rel.json"; export FIXTURE="$BASE/rel.json" ZIPFILE="$BASE/old.zip"
run_online old; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_out "$BASE/o-old" "does not include the Linux installer"
cmp -s "$G/BepInEx/plugins/LiarsBar8P.dll" "$BASE/prev.dll" && ok "previous plugin untouched" || nope "previous plugin changed"

echo "== a zip with Windows backslash paths, as v1.0.0's was: unpacked, then refused for having no install.sh"
reset_game
"$PYTHON" - "$REPO/dist/LiarsBar-8P.zip" "$BASE/backslash.zip" <<'PY'
import sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(src) as a, zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as b:
    for i in a.infolist():
        if i.filename.endswith(".sh") or i.filename.endswith("/"):
            continue
        info = zipfile.ZipInfo(i.filename.replace("/", "\\"), i.date_time)
        info.create_system = 0
        b.writestr(info, a.read(i))
PY
release_json "$GOOD" 1 > "$BASE/rel.json"; export FIXTURE="$BASE/rel.json" ZIPFILE="$BASE/backslash.zip"
run_online backslash; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
expect_out "$BASE/o-backslash" "does not include the Linux installer"
expect_not_out "$BASE/o-backslash" "Could not unpack"
cmp -s "$G/BepInEx/plugins/LiarsBar8P.dll" "$BASE/prev.dll" && ok "previous plugin untouched" || nope "previous plugin changed"

echo "== a download that is not a zip at all"
reset_game; printf '<html>rate limited</html>' > "$BASE/notzip"; export ZIPFILE="$BASE/notzip"
run_online junk; rc=$?
[ $rc -ne 0 ] && ok "non-zero exit" || nope "exit 0"
cmp -s "$G/BepInEx/plugins/LiarsBar8P.dll" "$BASE/prev.dll" && ok "previous plugin untouched" || nope "previous plugin changed"

echo
echo "RESULT: $PASS passed, $FAILS failed"
[ "$FAILS" -eq 0 ]
