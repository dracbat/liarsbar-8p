#!/usr/bin/env bash
#
#  Liar's Bar - 8 Player Mod installer, for Linux and the Steam Deck.
#
#      bash install.sh                  find the game through Steam
#      bash install.sh /path/to/game    or say where it is
#
#  Liar's Bar has no Linux version: on Linux it is the Windows game running under Proton,
#  and this mod is the same Windows mod, unchanged. So this does what install.bat does on
#  Windows - finds the game through Steam's own library files and copies the loader and
#  plugin in - and then checks the one thing Windows does not need: a Steam launch option
#  that tells Proton to load the mod's loader.
#
#  No file that belongs to the GAME is modified or deleted. (Steam's "Verify integrity of
#  game files" checks only those, so it does not remove this mod - uninstall.sh does.) It
#  does remove a previous copy of THIS mod
#  first: its own plugin and config, then the bundled BepInEx loader and dotnet runtime are
#  overwritten. Other mods' plugins are left alone.
#
#  Nothing here needs root, and it refuses to run as root: the game folder belongs to you,
#  and files written into it by root could not be updated by the game or by Steam.

set -u

APPID=3097560
EXE="Liar's Bar.exe"
LAUNCH_OPTION='WINEDLLOVERRIDES="winhttp=n,b" %command%'

if [ -t 1 ]; then
    C_OK=$'\e[32m'; C_BAD=$'\e[31m'; C_WARN=$'\e[33m'; C_HEAD=$'\e[36m'; C_DIM=$'\e[90m'; C_END=$'\e[0m'
else
    C_OK=; C_BAD=; C_WARN=; C_HEAD=; C_DIM=; C_END=
fi
say()   { printf '%s\n' "$*"; }
head_() { printf '%s%s%s\n' "$C_HEAD" "$*" "$C_END"; }
dim()   { printf '%s%s%s\n' "$C_DIM" "$*" "$C_END"; }
good()  { printf '%s  [OK]   %s%s\n' "$C_OK" "$*" "$C_END"; }
bad()   { printf '%s  [FAIL] %s%s\n' "$C_BAD" "$*" "$C_END"; }
warn()  { printf '%s  [!]    %s%s\n' "$C_WARN" "$*" "$C_END"; }

# Prompts read from the terminal itself rather than stdin, so they still work when the
# script was started by something that has stdin pointed elsewhere.
ask() {
    local reply=
    if { exec 3< /dev/tty; } 2> /dev/null; then
        read -r -p "$1" reply <&3 || reply=
        exec 3<&-
    else
        read -r -p "$1" reply || reply=
    fi
    printf '%s' "$reply"
}

# >>> finding the game
#     This block is identical in install.sh, uninstall.sh and Install-LiarsBar8P.sh.
#     Change all three together.

# A folder's real location with every symlink resolved, or nothing if it is not there.
canon() { ( CDPATH= cd -P -- "$1" 2>/dev/null && pwd -P ); }

# A library's steamapps folder. Very old installs called it SteamApps, and Linux cares.
steamapps_of() {
    local s
    for s in "$1/steamapps" "$1"/[Ss][Tt][Ee][Aa][Mm][Aa][Pp][Pp][Ss]; do
        if [ -d "$s" ]; then printf '%s\n' "$s"; return 0; fi
    done
    return 1
}

# Every place a Steam install is known to live: an explicit $STEAM_DIR, the native client,
# the Debian/Ubuntu package, the Flatpak and the Snap. On a Steam Deck it is the native one.
# ~/.steam/steam and ~/.steam/root are usually symlinks to one of the others, so roots are
# de-duplicated by where they really point.
steam_roots() {
    local d c seen=$'\n'
    for d in ${STEAM_DIR:+"$STEAM_DIR"} \
             "$HOME/.steam/steam" \
             "$HOME/.steam/root" \
             "${XDG_DATA_HOME:-$HOME/.local/share}/Steam" \
             "$HOME/.local/share/Steam" \
             "$HOME/.steam/debian-installation" \
             "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
             "$HOME/.var/app/com.valvesoftware.Steam/data/Steam" \
             "$HOME/snap/steam/common/.local/share/Steam" \
             "$HOME/.snap/data/steam/common/.local/share/Steam"
    do
        c="$(canon "$d")"
        [ -n "$c" ] && steamapps_of "$c" > /dev/null || continue
        case "$seen" in *$'\n'"$c"$'\n'*) continue ;; esac
        seen="$seen$c"$'\n'
        printf '%s\n' "$c"
    done
}

# Steam's .vdf and .acf files are made of   "key"   "value"   lines, and a backslash or a
# double quote inside a value is escaped with a backslash. These read a key's values back.
vdf_unescape() { sed 's/\\\\/\x01/g; s/\\"/"/g; s/\x01/\\/g'; }
vdf_value() {
    tr -d '\r' < "$2" 2> /dev/null \
        | sed -n 's/^[[:space:]]*"'"$1"'"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/Ip' | vdf_unescape
}

# Every library a Steam root knows about: the root itself, and every "path" in its
# libraryfolders.vdf - other drives, and the SD card on a Steam Deck. Steam before 2021
# wrote them as numbered keys instead. A library that is listed but not there is printed
# with a leading "-" so it can be reported: usually an SD card or a drive not plugged in.
root_libraries() {
    local root="$1" vdf lib c flat apps
    flat="$(canon "$HOME/.var/app/com.valvesoftware.Steam")"
    apps="$(steamapps_of "$root")"
    {
        printf '%s\n' "$root"
        for vdf in "$apps/libraryfolders.vdf" "$root/config/libraryfolders.vdf"; do
            [ -f "$vdf" ] || continue
            vdf_value path "$vdf"
            tr -d '\r' < "$vdf" \
                | sed -n 's/^[[:space:]]*"[0-9][0-9]*"[[:space:]]*"\(\/.*\)"[[:space:]]*$/\1/p' | vdf_unescape
        done
    } | while IFS= read -r lib; do
        [ -n "$lib" ] || continue
        # Inside the Flatpak, Steam's own folder is called ~/.local/share/Steam. Outside it,
        # that is a different Steam altogether, or nothing.
        if [ -n "$flat" ] && [ "$lib" = "$HOME/.local/share/Steam" ]; then
            case "$root" in "$flat"/*) lib="$root" ;; esac
        fi
        c="$(canon "$lib")"
        if [ -n "$c" ]; then printf '%s\n' "$c"; else printf -- '-%s\n' "$lib"; fi
    done
}

# The game folder a steamapps folder's manifest names - if the game is really in it.
manifest_game() {
    local dir
    [ -f "$1/appmanifest_$APPID.acf" ] || return 1
    dir="$(vdf_value installdir "$1/appmanifest_$APPID.acf" | head -n 1)"
    [ -n "$dir" ] && [ -f "$1/common/$dir/$EXE" ] || return 1
    canon "$1/common/$dir"
}

# Every install of the game, one real folder per line. A library whose appmanifest declares
# the app is believed; only if none does, a folder with the usual name and the exe in it.
find_games() {
    local root lib apps c m found= fallback=
    while IFS= read -r root; do
        while IFS= read -r lib; do
            case "$lib" in -*) continue ;; esac
            apps="$(steamapps_of "$lib")" || continue
            if c="$(manifest_game "$apps")"; then found="$found$c"$'\n'; fi
            if [ -f "$apps/common/Liar's Bar/$EXE" ]; then
                fallback="$fallback$(canon "$apps/common/Liar's Bar")"$'\n'
            fi
        done < <(root_libraries "$root")
    done < <(steam_roots)

    if [ -z "$found$fallback" ]; then
        # Steam's list of libraries can be out of date: SteamOS has moved where it mounts SD
        # cards, and an old entry can point at the old place while the card is in. So, as a
        # last look, wherever drives get mounted - two folders deep, no further.
        for m in ${LB8P_MOUNT_ROOTS:-/run/media /media /mnt}; do
            for apps in "$m"/*/steamapps "$m"/*/*/steamapps \
                        "$m"/*/SteamLibrary/steamapps "$m"/*/*/SteamLibrary/steamapps; do
                if c="$(manifest_game "$apps")"; then found="$found$c"$'\n'; fi
            done
        done
    fi
    printf '%s' "${found:-$fallback}" | awk 'length($0) && !seen[$0]++'
}

missing_libraries() {
    local root lib
    while IFS= read -r root; do
        while IFS= read -r lib; do
            case "$lib" in -*) printf '%s\n' "${lib#-}" ;; esac
        done < <(root_libraries "$root")
    done < <(steam_roots) | awk 'length($0) && !seen[$0]++'
}

# Which Steam an install belongs to, in a few words, for when there is more than one.
install_kind() {
    case "$1" in
        */.var/app/com.valvesoftware.Steam/*) printf 'Flatpak Steam' ;;
        */snap/steam/*)                       printf 'Snap Steam' ;;
        /run/media/*|/media/*|/mnt/*)         printf 'SD card or other drive' ;;
        *)                                    printf 'Steam' ;;
    esac
}

# A pasted path may arrive in quotes, with a trailing slash, as a file:// link, or with ~
# for the home folder. A path dragged into a terminal arrives quoted, and the apostrophe in
# "Liar's Bar" then arrives as '\'' - which is put back too. One typed with tab completion
# arrives with a backslash before each space and apostrophe, and a file:// link arrives
# with its space as %20.
clean_path() {
    local p="$1"
    p="${p#"${p%%[![:space:]]*}"}"; p="${p%"${p##*[![:space:]]}"}"
    case "$p" in
        \"*\") p="${p#\"}"; p="${p%\"}" ;;
        \'*\') p="${p#\'}"; p="${p%\'}"; p="${p//\'\\\'\'/\'}" ;;
        *)     p="$(printf '%s' "$p" | sed 's/\\\(.\)/\1/g')" ;;
    esac
    case "$p" in file://*) p="${p#file://}"; p="$(printf '%b' "${p//%/\\x}")" ;; esac
    case "$p" in "~") p="$HOME" ;; "~/"*) p="$HOME/${p#\~/}" ;; esac
    while [ "${#p}" -gt 1 ] && [ "${p%/}" != "$p" ]; do p="${p%/}"; done
    printf '%s' "$p"
}

# Sets GAME: the folder named on the command line, else the one Steam has, else one the
# person pastes in. Never a folder without the game's exe in it - everything after this
# writes into it, or deletes from it. Exits if there is none.
locate_game() {
    local found=() i n reply roots typed
    if [ "$#" -ge 1 ] && [ -n "$1" ]; then
        typed="$(clean_path "$1")"
        if [ ! -f "$typed/$EXE" ]; then
            bad "No \"$EXE\" in $typed. Nothing was changed."
            exit 1
        fi
        GAME="$(canon "$typed")"
        return
    fi

    say "Looking for Liar's Bar..."
    roots="$(steam_roots)"
    if [ -n "$roots" ]; then
        while IFS= read -r i; do good "Steam: $i"; done <<< "$roots"
    else
        warn "Steam not found in any of the usual places"
    fi
    mapfile -t found < <(find_games)

    if [ "${#found[@]}" -eq 1 ]; then GAME="${found[0]}"; return; fi

    if [ "${#found[@]}" -gt 1 ]; then
        # Two Steams on one machine - a native one and a leftover Flatpak, say - happens,
        # and installing into the copy that never gets launched looks exactly like the mod
        # not working. So ask rather than guess.
        say ""
        warn "Liar's Bar is installed in more than one place:"
        n=0
        for i in "${found[@]}"; do n=$((n + 1)); say "     $n) $i   ($(install_kind "$i"))"; done
        say ""
        reply="$(ask "  Which one do you play? Type its number (or press Enter to cancel): ")"
        case "$reply" in ''|*[!0-9]*) say ""; bad "Cancelled."; exit 1 ;; esac
        if [ "$reply" -lt 1 ] || [ "$reply" -gt "$n" ]; then
            bad "There is no number $reply. Nothing was changed."
            exit 1
        fi
        GAME="${found[$((reply - 1))]}"
        return
    fi

    # last resort: let the person point at it
    bad "Could not find Liar's Bar automatically."
    while IFS= read -r i; do
        warn "Steam lists a library at $i, but it is not there - is that SD card or drive in?"
    done < <(missing_libraries)
    say ""
    dim "  In Steam: right click Liar's Bar -> Manage -> Browse local files,"
    dim "  then copy the folder's location and paste it below."
    say ""
    typed="$(clean_path "$(ask "  Paste the Liar's Bar folder path (or press Enter to cancel): ")")"
    if [ -z "$typed" ]; then say ""; bad "Cancelled."; exit 1; fi
    if [ ! -f "$typed/$EXE" ]; then
        bad "No \"$EXE\" in that folder. Nothing was changed."
        exit 1
    fi
    GAME="$(canon "$typed")"
}

# Under Proton the game is a Windows process, but it is still an ordinary process on Linux:
# its command line carries the .exe name, and Steam's launcher sits above it with the app
# id on its own command line for exactly as long as the game is running.
game_running() {
    local f pattern="Liar's Bar\.exe|AppId=$APPID( |$)"
    if command -v pgrep > /dev/null 2>&1; then
        pgrep -f "$pattern" > /dev/null 2>&1
        return
    fi
    for f in /proc/[0-9]*/cmdline; do
        tr '\0' ' ' < "$f" 2> /dev/null | grep -qE "$pattern" && return 0
    done
    return 1
}
# <<< finding the game

HERE="$(canon "$(dirname -- "${BASH_SOURCE[0]}")")"

say ""
head_ "=========================================="
head_ "  Liar's Bar - 8 Player Mod  :  Installer"
head_ "=========================================="
say ""

if [ "$(id -u)" -eq 0 ]; then
    bad "Do not run this as root, or with sudo."
    dim "  Files root puts in the game folder belong to root, and then neither the game"
    dim "  nor Steam can update them. Run it again as yourself:  bash install.sh"
    exit 1
fi

locate_game "$@"
good "Game: $GAME"

# ------------------------------------------------------------ safety checks
if game_running; then
    say ""
    bad "Liar's Bar is running. Close the game and run this again."
    exit 1
fi

if [ ! -d "$HERE/BepInEx" ] || [ ! -f "$HERE/winhttp.dll" ]; then
    say ""
    bad "This installer is missing its files (BepInEx / winhttp.dll)."
    dim "  Extract the whole zip first, then run install.sh from inside it."
    exit 1
fi

if [ ! -w "$GAME" ]; then
    say ""
    bad "Cannot write to the game folder."
    dim "  It may belong to another user, or have been changed by an earlier sudo - or it"
    dim "  is on a drive mounted read-only, such as a Windows drive shared with Linux."
    dim "  Do not use sudo for this; see who owns it with:"
    dim "      ls -ld $(printf '%q' "$GAME")"
    exit 1
fi

# ------------------------------------------------------------------ install
PAYLOAD=(BepInEx dotnet winhttp.dll doorstop_config.ini .doorstop_version changelog.txt)
CFG="$GAME/BepInEx/config/liarsbar.eightplayers.cfg"

say ""
if [ "$HERE" -ef "$GAME" ]; then
    # The zip extracted straight into the game folder is already a complete install - and
    # clearing out the previous plugin, below, would then delete the only copy of it.
    # Compared as folders, not as text: one folder can be reached by two paths.
    say "The mod's files are already in the game folder - nothing to copy."
else
    say "Installing..."
    # A stale plugin or leftover config has broken sessions before: an old setting
    # survived an update and silently disabled a fix. Every install starts from a clean
    # slate for this mod's own files. BepInEx's generated interop folder is left alone
    # on purpose - it is expensive to rebuild and is not ours.
    #
    # Earlier versions used a differently prefixed settings file, so config is matched on
    # the suffix, and any stray copy of the plugin goes too - in a subfolder, where mod
    # managers put it, or as a link, since BepInEx loads those as well. Matched without
    # regard to case, as Windows does, but on this mod's own name only: other Liar's Bar
    # mods stay. A link is removed, never what it points at.
    while IFS= read -r -d '' old; do
        if rm -f -- "$old"; then good "removed old ${old##*/}"
        else warn "could not remove ${old##*/}"; fi
    done < <(find "$GAME/BepInEx/config" -maxdepth 1 \( -type f -o -type l \) -iname '*liarsbar.eightplayers.cfg' -print0 2> /dev/null
             find "$GAME/BepInEx/plugins" -mindepth 1 \( -type f -o -type l \) -iname '*liarsbar8p*' -print0 2> /dev/null)

    # --remove-destination: where a file in the game folder is a symlink - a plugin linked
    # to someone's own build, a loader shared between games - replace the link rather than
    # write through it into whatever it points at, outside the game folder.
    for item in "${PAYLOAD[@]}"; do
        [ -e "$HERE/$item" ] || continue
        if ! cp -Rf --remove-destination -- "$HERE/$item" "$GAME/"; then
            say ""
            bad "Copy failed for $item."
            dim "  Check there is free space, and that the game folder is yours:"
            dim "      ls -ld $(printf '%q' "$GAME")"
            exit 1
        fi
    done
    good "Files copied"
fi

# ------------------------------------------------------------------- verify
say ""
say "Verifying..."
fail=0
check() { if [ -e "$2" ]; then good "$1"; else bad "$1"; fail=1; fi; }
check "8 Player plugin"       "$GAME/BepInEx/plugins/LiarsBar8P.dll"
check "BepInEx core"          "$GAME/BepInEx/core/BepInEx.Core.dll"
check "fresh config"          "$CFG"
check "winhttp.dll (loader)"  "$GAME/winhttp.dll"

say ""
if [ "$fail" -ne 0 ]; then
    printf '%sInstall INCOMPLETE - see the failures above.%s\n' "$C_BAD" "$C_END"
    exit 1
fi

mp="$(tr -d '\r' < "$CFG" | sed -n 's/^[[:space:]]*MaxPlayers[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' | head -n 1)"

# ------------------------------------------------------------ launch option
# Proton has a winhttp.dll of its own and uses it in preference to the one in the game
# folder, so without this option the loader is never started and the game runs as if the
# mod were not there - no error, no sign of anything wrong. Steam keeps launch options in
# each account's localconfig.vdf. This only READS it: Steam rewrites that file while it
# runs, and would undo an edit made underneath it.
#
# Only the working form counts as set: one WINEDLLOVERRIDES (a second one replaces the
# first), winhttp in it as native-then-builtin - alone or in a list such as
# "winhttp,dxgi=n,b" - and %command%. "winhttp=n" alone, or no %command%, fails in exactly
# the silent way above, so an option that mentions winhttp without being right is reported
# as wrong, not as done.
#
# Only the Steam that owns the game is read - a second Steam on the machine with the option
# set says nothing about the one that launches the game - and every account in it that has
# Liar's Bar has to have it.
game_roots() {
    local root lib
    while IFS= read -r root; do
        while IFS= read -r lib; do
            case "$lib" in -*) continue ;; esac
            case "$GAME/" in "$lib"/*) printf '%s\n' "$root"; break ;; esac
        done < <(root_libraries "$root")
    done < <(steam_roots)
}
launch_option_state() {
    local roots root cfg grade n_set=0 n_wrong=0 n_unset=0 any=0
    roots="$(game_roots)"
    # A folder typed in by hand may sit outside every library Steam lists; then all of them.
    [ -n "$roots" ] || roots="$(steam_roots)"
    while IFS= read -r root; do
        [ -n "$root" ] || continue
        for cfg in "$root"/userdata/*/config/localconfig.vdf; do
            [ -f "$cfg" ] || continue
            any=1
            # 0: this account has no Liar's Bar entry; 1: no usable option; 2: wrong; 3: set
            grade="$(tr -d '\r' < "$cfg" | awk -v id="\"$APPID\"" '
                    $1 == id { inapp = 1; seen = 1; depth = 0; next }
                    inapp && /\{/ { depth++ }
                    inapp && /\}/ { depth--; if (depth <= 0) inapp = 0 }
                    inapp && /"LaunchOptions"/ {
                        line = $0
                        overrides = gsub(/WINEDLLOVERRIDES=/, "&", line)
                        # Only what comes before %command% is the environment; anything
                        # after it is handed to the game as an argument. The value may be
                        # double quoted, single quoted (char 39) or bare.
                        before = index(line, "WINEDLLOVERRIDES=") < index(line, "%command%")
                        re = "(^|[\"" sprintf("%c", 39) "\\\\;,= ])([A-Za-z0-9_.]+,)*winhttp(\\.dll)?(,[A-Za-z0-9_.]+)*=n(ative)?,b(uiltin)?"
                        if (overrides == 1 && before && line ~ re) hit = 3
                        else if (/winhttp|WINEDLLOVERRIDES/) hit = 2
                    }
                    END { print seen ? (hit ? hit : 1) : 0 }')"
            case "$grade" in
                3) n_set=$((n_set + 1)) ;;
                2) n_wrong=$((n_wrong + 1)) ;;
                1) n_unset=$((n_unset + 1)) ;;
            esac
        done
    done <<< "$roots"
    if [ "$n_wrong" -gt 0 ]; then echo wrong
    elif [ "$n_set" -gt 0 ] && [ "$n_unset" -gt 0 ]; then echo partial
    elif [ "$n_set" -gt 0 ]; then echo set
    elif [ "$any" -eq 1 ]; then echo unset
    else echo unknown
    fi
}

printf '%s==========================================%s\n' "$C_OK" "$C_END"
printf '%s  Installed successfully - max %s players%s\n' "$C_OK" "${mp:-8}" "$C_END"
printf '%s==========================================%s\n' "$C_OK" "$C_END"
say ""

state="$(launch_option_state)"
if [ "$state" = set ]; then
    good "Steam launch option is already set"
    say ""
else
    printf '%sONE MORE STEP - without it the mod does not load:%s\n' "$C_WARN" "$C_END"
    say ""
    if [ "$state" = wrong ]; then
        warn "Liar's Bar has a launch option that mentions winhttp, but it is not quite"
        warn "right - Proton will ignore the mod. Replace it with the line below."
        say ""
    elif [ "$state" = partial ]; then
        warn "It is set for one Steam account on this machine but not another. If you"
        warn "play Liar's Bar on the other one, set it there too."
        say ""
    fi
    say "  In Steam, right click Liar's Bar -> Properties -> General -> Launch Options,"
    say "  and paste in exactly:"
    say ""
    printf '      %s%s%s\n' "$C_HEAD" "$LAUNCH_OPTION" "$C_END"
    say ""
    dim "  Steam Deck in Game Mode: select Liar's Bar, press the cog -> Properties."
    if [ "$state" = unset ]; then
        dim "  (If you set it a moment ago, Steam may not have saved it yet - that is fine.)"
    fi
    say ""
fi

# On a Steam Deck the top graphics preset does not fit. Loading the table with Ultra
# graphics ran a Deck out of graphics memory - with four players as well as eight, so it is
# the setting rather than the table size - and the game sat on its loading screen for good.
# High fitted an eight player table in about 7 GB of the 9 GB the Deck's GPU can use.
if [ "$(cat /sys/devices/virtual/dmi/id/board_vendor 2> /dev/null)" = Valve ]; then
    printf '%sSTEAM DECK:%s\n' "$C_WARN" "$C_END"
    say "  Set the game's graphics to High or lower (Settings -> Graphics). On Ultra the"
    say "  Deck runs out of graphics memory loading the table, and the game hangs on its"
    say "  loading screen."
    say ""
fi

head_ "NEXT:"
say "  1. Launch Liar's Bar."
say "  2. The FIRST launch is slow (a few minutes) while it sets up."
say "     This happens once. Let it reach the main menu."
say "  3. The mod's version is drawn in the TOP LEFT corner in game."
say "     If it is not there, the launch option above is missing or mistyped."
say ""
printf '%sIMPORTANT:%s\n' "$C_WARN" "$C_END"
printf '%s  Everyone you play with needs this same mod, on the same version,%s\n' "$C_WARN" "$C_END"
printf '%s  with the same MaxPlayers value - on Windows or on Linux.%s\n' "$C_WARN" "$C_END"
say ""
dim "  Settings: $CFG"
dim "  Log:      $GAME/BepInEx/LogOutput.log"
# Run by the online installer, this folder is a temporary one about to be deleted, and the
# uninstaller in it goes too; that installer says how to remove the mod instead.
[ -n "${LB8P_ONLINE:-}" ] || dim "  To remove: bash uninstall.sh"
