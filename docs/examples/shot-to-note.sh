#!/usr/bin/env bash
# Linux: image (or grim + slurp selection) -> kscan -> dated Markdown note.
# bash shot-to-note.sh -k /path/to/kscan [-i code.png] [-n ./notes]
set -euo pipefail
kscan_bin="${KSCAN_BIN:-kscan}"
note_dir="${HOME}/notes"
image=""
usage() { printf '%s\n' 'Usage: shot-to-note.sh [-k kscan] [-i image] [-n notes-directory]'; }
while (($#)); do
    case "$1" in
        -k|--kscan|-i|--image|-n|--note)
            (($# >= 2)) || { usage >&2; exit 2; }
            case "$1" in
                -k|--kscan) kscan_bin="$2" ;;
                -i|--image) image="$2" ;;
                -n|--note) note_dir="$2" ;;
            esac
            shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
command -v "$kscan_bin" >/dev/null || { printf 'Cannot find kscan: %s\n' "$kscan_bin" >&2; exit 2; }
temporary=""
cleanup() { if [[ -n "$temporary" ]]; then rm -f -- "$temporary"; fi; }
trap cleanup EXIT
if [[ -z "$image" ]]; then
    command -v grim >/dev/null && command -v slurp >/dev/null || { echo 'Install grim and slurp, or use -i.' >&2; exit 2; }
    if ! region="$(slurp)"; then echo 'Selection canceled.'; exit 0; fi
    [[ -n "$region" ]] || exit 0
    temporary="$(mktemp --suffix=.png)"
    image="$temporary"
    grim -g "$region" "$image"
fi
[[ -f "$image" ]] || { echo 'Image not found.' >&2; exit 2; }
if text="$("$kscan_bin" decode "$image")"; then
    [[ -n "$text" ]] || exit 1
else
    status=$?
    printf 'Decode failed (%s); note unchanged.\n' "$status" >&2
    exit "$status"
fi
mkdir -p -- "$note_dir"
file="${note_dir}/$(date +%F).md"
{
    printf '## %s\n\n' "$(date +%T)"
    while IFS= read -r line || [[ -n "$line" ]]; do printf '> %s\n' "$line"; done <<< "$text"
    printf '\n'
} >> "$file"
printf 'Appended to %s\n' "$file"
