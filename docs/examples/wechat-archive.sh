#!/usr/bin/env bash
# Linux/GNU: explicit source -> date/category archive. Copy is the default.
# bash wechat-archive.sh -s ./inbox -t ./archive [-n] [-m] [-d]
set -euo pipefail
source_dir=""
destination=""
dry_run=0
move_files=0
by_day=0
usage() { printf '%s\n' 'Usage: wechat-archive.sh -s source -t destination [-n dry-run] [-m move] [-d by-day]'; }
while (($#)); do
    case "$1" in
        -s|--source|-t|--dest)
            (($# >= 2)) || { usage >&2; exit 2; }
            if [[ "$1" == -s || "$1" == --source ]]; then source_dir="$2"; else destination="$2"; fi
            shift 2 ;;
        -n|--dry-run) dry_run=1; shift ;;
        -m|--move) move_files=1; shift ;;
        -d|--by-day) by_day=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
[[ -d "$source_dir" && -n "$destination" ]] || { usage >&2; exit 2; }
for tool in realpath find date sha256sum; do command -v "$tool" >/dev/null || { echo "Missing: $tool" >&2; exit 2; }; done
source_dir="$(realpath -- "$source_dir")"
destination="$(realpath -m -- "$destination")"
case "$destination/" in "$source_dir/"*) echo 'Destination must be outside source.' >&2; exit 2 ;; esac
category() {
    case "${1,,}" in
        png|jpg|jpeg|gif|bmp|webp|heic|tif|tiff) echo images ;;
        pdf|doc|docx|xls|xlsx|ppt|pptx|txt|md|rtf|csv|odt) echo documents ;;
        zip|rar|7z|tar|gz|xz|bz2) echo archives ;;
        mp3|wav|flac|m4a|mp4|mov|avi|mkv|webm) echo media ;;
        ps1|sh|py|js|ts|cs|json|xml|html|css|yaml|yml) echo code ;;
        *) echo other ;;
    esac
}
processed=0
skipped=0
while IFS= read -r -d '' file; do
    base="${file##*/}"
    extension="${base##*.}"
    case "${extension,,}" in tmp|part|crdownload) skipped=$((skipped + 1)); continue ;; esac
    format='%Y-%m'
    if ((by_day)); then format='%F'; fi
    date_dir="$(date -r "$file" +"$format")"
    folder="${destination}/${date_dir}/$(category "$extension")"
    target="${folder}/${base}"
    stem="$base"
    suffix=""
    if [[ "$base" == *.* && "$base" != .* ]]; then stem="${base%.*}"; suffix=".${extension}"; fi
    duplicate=0
    counter=0
    while [[ -e "$target" ]]; do
        if [[ -f "$target" ]] && [[ "$(sha256sum < "$file")" == "$(sha256sum < "$target")" ]]; then duplicate=1; break; fi
        counter=$((counter + 1))
        target="${folder}/${stem}-${counter}${suffix}"
    done
    if ((duplicate)); then skipped=$((skipped + 1)); continue; fi
    action=copy
    if ((move_files)); then action=move; fi
    printf '%s: %s -> %s\n' "$action" "$file" "$target"
    if ((dry_run)); then continue; fi
    mkdir -p -- "$folder"
    if ((move_files)); then mv -n -- "$file" "$target"; else cp -p -n -- "$file" "$target"; fi
    processed=$((processed + 1))
done < <(find "$source_dir" -type f -print0)
printf 'Processed %s; skipped %s.\n' "$processed" "$skipped"
