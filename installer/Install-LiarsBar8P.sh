#!/usr/bin/env bash
#
#  Liar's Bar - 8 Player Mod : online installer for Linux and the Steam Deck.
#
#      bash Install-LiarsBar8P.sh
#
#  Downloads the latest release from GitHub and installs it, replacing any previous copy
#  of this mod. Keep this file: running it again is how you update.
#
#  It is a plain text file - open it in a text editor first if you want to read exactly
#  what it does. In short: it finds Liar's Bar through Steam, asks GitHub for the newest
#  release, downloads that release's zip from GitHub and nowhere else, unpacks it, and
#  runs the install.sh inside it.

set -u

REPO='dracbat/liarsbar-8p'
ASSET='LiarsBar-8P.zip'
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
head_ "================================================="
head_ "  Liar's Bar - 8 Player Mod  :  Online Installer"
head_ "================================================="
say ""

if [ "$(id -u)" -eq 0 ]; then
    bad "Do not run this as root, or with sudo."
    dim "  Files root puts in the game folder belong to root, and then neither the game"
    dim "  nor Steam can update them. Run it again as yourself:  bash Install-LiarsBar8P.sh"
    exit 1
fi

locate_game "$@"
good "Game: $GAME"

if game_running; then
    say ""
    bad "Liar's Bar is running. Close the game and run this again."
    exit 1
fi

# -------------------------------------------------------------- tools needed
# Downloading: curl, or failing that Python. Either is held to https the whole way,
# redirects included. (wget, the usual third choice, follows a redirect from https to plain
# http without a word, so it is not used.)
if command -v curl > /dev/null 2>&1; then FETCH=curl
elif command -v python3 > /dev/null 2>&1; then FETCH=python3
else
    bad "Neither curl nor python3 is installed, so nothing can be downloaded."
    dim "  Install curl with your package manager, or use the zip instead:"
    dim "  https://github.com/$REPO/releases/latest"
    exit 1
fi

# Prints the HTTP status of the answer; a non-zero exit means no answer came back at all.
fetch() {  # $1 = https URL, $2 = file to save it to
    if [ "$FETCH" = curl ]; then
        curl -sS -L --proto '=https' --proto-redir '=https' -H 'User-Agent: LiarsBar8P-Installer' \
             -o "$2" -w '%{http_code}' "$1"
        return
    fi
    python3 - "$1" "$2" << 'PY'
import shutil, sys, urllib.error, urllib.request
url, out = sys.argv[1], sys.argv[2]
class HttpsOnly(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not newurl.lower().startswith("https://"):
            raise urllib.error.URLError("refused a redirect away from https")
        return super().redirect_request(req, fp, code, msg, headers, newurl)
request = urllib.request.Request(url, headers={"User-Agent": "LiarsBar8P-Installer"})
try:
    with urllib.request.build_opener(HttpsOnly).open(request, timeout=120) as answer, open(out, "wb") as f:
        shutil.copyfileobj(answer, f)
        print(answer.status)
except urllib.error.HTTPError as e:
    print(e.code)
except Exception as e:
    sys.stderr.write("%s\n" % e)
    sys.exit(1)
PY
}

# Unzipping: whichever of these is present.
if command -v unzip > /dev/null 2>&1; then UNPACK=unzip
elif command -v bsdtar > /dev/null 2>&1; then UNPACK=bsdtar
elif command -v python3 > /dev/null 2>&1; then UNPACK=python3
else
    bad "Nothing that can open a .zip is installed (unzip, bsdtar or python3)."
    dim "  Install unzip with your package manager, then run this again."
    exit 1
fi
unpack() {
    case "$UNPACK" in
        # unzip exits 1 for a warning it has dealt with - an archive made on Windows, whose
        # backslash paths it has just put right, is one - and 2 or more for a real failure.
        unzip)   unzip -q -o "$1" -d "$2"; [ $? -le 1 ] ;;
        bsdtar)  bsdtar -xf "$1" -C "$2" ;;
        python3) python3 -m zipfile -e "$1" "$2" ;;
    esac
}

TMP="$(mktemp -d "${TMPDIR:-/tmp}/LiarsBar8P.XXXXXXXX")" || { bad "Could not make a temporary folder."; exit 1; }
trap 'rm -rf -- "$TMP"' EXIT

# ------------------------------------------------------------ latest release
say ""
say "Checking for the latest release..."
code="$(fetch "https://api.github.com/repos/$REPO/releases/latest" "$TMP/release.json")"
rc=$?
if [ "$code" = 404 ]; then
    bad "No published release found for $REPO."
    dim "  Either no release has been published yet, or the repository is private."
    exit 1
fi
if [ "$rc" -ne 0 ] || [ "$code" != 200 ]; then
    bad "Could not reach GitHub${code:+ (HTTP $code)}."
    dim "  Check your internet connection and try again."
    exit 1
fi

# GitHub's answer is JSON. Rather than depend on a JSON tool that may not be installed,
# pick out the fields needed: a double quote inside any JSON string is escaped, so these
# patterns can only match real keys, never text quoted in the release notes.
json_field() { grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | sed 's/.*"\([^"]*\)"$/\1/'; }
TAG="$(json_field tag_name < "$TMP/release.json" | head -n 1)"
# The tag is printed to the terminal, so it may only be what a version tag looks like.
case "$TAG" in *[!A-Za-z0-9._+-]*) TAG= ;; esac
URLS="$(json_field browser_download_url < "$TMP/release.json")"
# The mod's own zip by name; failing that, the first zip, which is what the Windows
# installer takes.
URL="$(printf '%s\n' "$URLS" | grep "/$ASSET\$" | head -n 1)"
[ -n "$URL" ] || URL="$(printf '%s\n' "$URLS" | grep '\.zip$' | head -n 1)"
if [ -z "$URL" ]; then bad "That release has no .zip attached to it."; exit 1; fi

# ------------------------------------------------------------------ download
# Where the download may come from is decided here, not by the response.
#
# This script asks GitHub's API for the newest release and then downloads whatever URL
# comes back, and installs it - including running the install.sh inside it. That is fine
# right up until the answer is not this project's. So the link has to be exactly the form
# GitHub gives this repository's release files - https://github.com/<this repo>/releases/
# download/<tag>/<file> - and anything else, another host or another repository on GitHub,
# stops the install before anything is fetched. GitHub then redirects the download to its
# file servers, which is followed over https only. Should the repository ever be renamed,
# this fails closed, and the installer has to be downloaded again.
want="https://github.com/$REPO/releases/download/"
rest="${URL#"$want"}"
tag_part="${rest%%/*}"
file_part="${rest#*/}"
url_ok=0
if [ "$rest" != "$URL" ] && [ "$tag_part" != "$rest" ] \
   && [[ "$tag_part" =~ ^[A-Za-z0-9._+-]+$ ]] && [[ "$file_part" =~ ^[A-Za-z0-9._+-]+\.zip$ ]] \
   && [ "$tag_part" != . ] && [ "$tag_part" != .. ] \
   && { [ -z "$TAG" ] || [ "$tag_part" = "$TAG" ]; }; then
    url_ok=1
fi
if [ "$url_ok" -ne 1 ]; then
    bad "The download link is not one of this project's own releases on GitHub:"
    dim "      $(printf '%s' "$URL" | tr -cd '[:print:]' | cut -c 1-200)"
    bad "Nothing has been downloaded or installed. Please report this."
    exit 1
fi
good "Version ${TAG:-unknown}"

say ""
say "Downloading..."
ZIP="$TMP/mod.zip"
code="$(fetch "$URL" "$ZIP")"
rc=$?
if [ "$rc" -ne 0 ] || [ "$code" != 200 ] || [ ! -s "$ZIP" ]; then
    bad "Download failed${code:+ (HTTP $code)}. Check your internet connection and try again."
    exit 1
fi
good "Downloaded $(( $(wc -c < "$ZIP") / 1048576 )) MB"

say ""
say "Extracting..."
mkdir -p "$TMP/x"
if ! unpack "$ZIP" "$TMP/x" > /dev/null 2>&1; then
    bad "Could not unpack the download. Nothing has been changed."
    exit 1
fi
SRC="$TMP/x"
if [ ! -d "$SRC/BepInEx" ]; then
    for d in "$TMP"/x/*/; do
        if [ -d "$d/BepInEx" ]; then SRC="${d%/}"; break; fi
    done
fi
if [ ! -d "$SRC/BepInEx" ]; then
    bad "The downloaded package does not look right (no BepInEx folder)."
    exit 1
fi
if [ ! -f "$SRC/install.sh" ]; then
    bad "That release does not include the Linux installer (install.sh)."
    dim "  It predates Linux support. Nothing has been changed."
    exit 1
fi
good "Extracted"

# Only now, with the new release downloaded, unpacked and checked, is anything in the game
# folder touched: the installer inside it clears out the previous copy and puts this one in.
# Never take the working copy away before the new one is in hand - a player whose download
# failed half way would otherwise be left with a game that looks normal and is in effect a
# vanilla client, and would desync the first lobby they joined.
LB8P_ONLINE=1 bash "$SRC/install.sh" "$GAME" || exit $?

if [ -n "$TAG" ]; then
    head_ "Installed $TAG."
    say "  The TOP LEFT of the screen in game must read $TAG."
else
    head_ "Installed the latest release."
fi
say "  Everyone playing together must show the same version."
say ""
head_ "TO UPDATE LATER: run this same file again."
dim "  It always fetches whatever the newest release is, so keep it somewhere handy"
dim "  rather than downloading it again each time."
say ""
dim "  To remove the mod: download LiarsBar-8P.zip from the release page and run"
dim "  bash uninstall.sh from inside it, or see Uninstall in the README."
