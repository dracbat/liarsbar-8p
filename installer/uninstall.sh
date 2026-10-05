#!/usr/bin/env bash
#
#  Removes the 8 Player mod from Liar's Bar on Linux and the Steam Deck, and the BepInEx
#  loader with it if no other mod is using it. Game files are never touched, and neither
#  is anybody else's plugin.
#
#      bash uninstall.sh                  find the game through Steam
#      bash uninstall.sh /path/to/game    or say where it is

set -u

APPID=3097560
EXE="Liar's Bar.exe"

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

say ""
head_ "============================================"
head_ "  Liar's Bar - 8 Player Mod  :  Uninstaller"
head_ "============================================"
say ""

if [ "$(id -u)" -eq 0 ]; then
    bad "Do not run this as root, or with sudo. Run it as yourself:  bash uninstall.sh"
    exit 1
fi

locate_game "$@"
good "Game: $GAME"

if game_running; then
    say ""
    bad "Liar's Bar is running. Close it and run this again."
    exit 1
fi

say ""
say "Removing mod files..."

# This mod's own files first: the plugin, its settings, and any older copy of either -
# the same set the installer clears, so a leftover copy of this mod cannot be mistaken
# below for somebody else's plugin and keep the loader installed.
removed=0
while IFS= read -r -d '' f; do
    if rm -f -- "$f"; then good "removed ${f##*/}"; removed=$((removed + 1))
    else bad "could not remove ${f##*/}"; fi
done < <(find "$GAME/BepInEx/plugins" -mindepth 1 \( -type f -o -type l \) -iname '*liarsbar8p*' -print0 2> /dev/null
         find "$GAME/BepInEx/config" -maxdepth 1 \( -type f -o -type l \) -iname '*liarsbar.eightplayers.cfg' -print0 2> /dev/null)

# BepInEx itself is shared, so it only goes if nothing else is using it. With this mod's
# own files gone, anything still in plugins/ or patchers/ is somebody else's - a DLL, one
# switched off by renaming it, a folder of them, a link to one kept elsewhere - and keeps
# the loader installed. Only this mod's own files go.
others=()
while IFS= read -r -d '' p; do others+=("$p"); done < <(
    find "$GAME/BepInEx/plugins" "$GAME/BepInEx/patchers" -mindepth 1 \( -type f -o -type l \) -print0 2> /dev/null)

kept=()
if [ "${#others[@]}" -gt 0 ]; then
    say ""
    printf '%sLeaving BepInEx in place - %s other mod file(s) are using it:%s\n' "$C_WARN" "${#others[@]}" "$C_END"
    for p in "${others[@]}"; do dim "         ${p#"$GAME/BepInEx/"}"; done
else
    for t in dotnet winhttp.dll doorstop_config.ini .doorstop_version changelog.txt; do
        if [ -e "$GAME/$t" ] || [ -L "$GAME/$t" ]; then
            if rm -rf -- "${GAME:?}/$t"; then good "removed $t"; removed=$((removed + 1))
            else bad "could not remove $t"; fi
        fi
    done
    # The rest of BepInEx is the loader's own - its core, the interop it generated, its
    # cache, its log, its own settings - except for other mods' settings files, which
    # outlive their mods and are kept in case the mod comes back.
    while IFS= read -r -d '' p; do kept+=("$p"); done < <(
        find "$GAME/BepInEx/config" -mindepth 1 -maxdepth 1 ! -iname 'BepInEx.cfg' -print0 2> /dev/null)
    if [ -d "$GAME/BepInEx" ] || [ -L "$GAME/BepInEx" ]; then
        if [ "${#kept[@]}" -eq 0 ]; then
            if rm -rf -- "${GAME:?}/BepInEx"; then good "removed BepInEx"; removed=$((removed + 1))
            else bad "could not remove BepInEx"; fi
        elif find "$GAME/BepInEx" -mindepth 1 -maxdepth 1 ! -name config -exec rm -rf -- {} + \
                && rm -f -- "$GAME/BepInEx/config/BepInEx.cfg"; then
            good "removed BepInEx, keeping other mods' settings:"
            for p in "${kept[@]}"; do dim "         config/${p##*/}"; done
            removed=$((removed + 1))
        else
            bad "could not remove all of BepInEx"
        fi
    fi
fi

say ""
if [ "$removed" -eq 0 ]; then
    printf '%sNothing to remove - the mod was not installed.%s\n' "$C_WARN" "$C_END"
else
    printf '%s============================================%s\n' "$C_OK" "$C_END"
    if [ "${#others[@]}" -gt 0 ]; then
        printf '%s  8 Player mod removed. BepInEx and your other%s\n' "$C_OK" "$C_END"
        printf '%s  mods were left alone.%s\n' "$C_OK" "$C_END"
    elif [ "${#kept[@]}" -gt 0 ]; then
        printf '%s  Uninstalled. The game is back to vanilla; other%s\n' "$C_OK" "$C_END"
        printf '%s  mods'"'"' settings were kept in BepInEx/config.%s\n' "$C_OK" "$C_END"
    else
        printf '%s  Uninstalled. The game is back to vanilla.%s\n' "$C_OK" "$C_END"
    fi
    printf '%s============================================%s\n' "$C_OK" "$C_END"
    say ""
    dim "  The game's own files were never modified."
fi
if [ "${#others[@]}" -eq 0 ]; then
    say ""
    dim "  The Steam launch option WINEDLLOVERRIDES=\"winhttp=n,b\" %command% can go too."
    dim "  It does nothing once the loader is gone, so leaving it is harmless."
fi
