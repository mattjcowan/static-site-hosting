#!/usr/bin/env bash
#
# Zips the sample sites into ready-to-deploy archives at the root of the repo.
#
#   scripts/build-samples.sh            # all of them
#   scripts/build-samples.sh blog-site  # just one
#
# Each archive holds the folder's contents (not the folder itself), without dotfiles or the
# folder's README, which is for people reading the repo rather than for visitors.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# sample folder -> archive name. demo-site keeps its historical name, which the README uses.
declare -A archives=(
  [demo-site]=sample-site.zip
  [blog-site]=blog-site.zip
)

if ! command -v zip >/dev/null; then
  echo "zip is not installed (try: sudo apt install zip)" >&2
  exit 1
fi

names=("$@")
[[ ${#names[@]} -eq 0 ]] && names=(demo-site blog-site)

for name in "${names[@]}"; do
  archive="${archives[$name]:-}"
  if [[ -z "$archive" ]]; then
    echo "Unknown sample '$name'. Known: ${!archives[*]}" >&2
    exit 1
  fi

  source="$root/samples/$name"
  target="$root/$archive"

  rm -f "$target"
  (cd "$source" && zip -qrX "$target" . -x '.*' '*/.*' 'README.md')

  files=$(unzip -Z1 "$target" | grep -vc '/$')
  size=$(du -h "$target" | cut -f1)
  echo "$archive  ←  samples/$name  ($files files, $size)"
done
