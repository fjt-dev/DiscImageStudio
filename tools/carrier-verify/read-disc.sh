#!/usr/bin/env bash
# Reads a data disc into a 2048-byte-sector image, zero-filling sectors the drive cannot read.
#
#   read-disc.sh DEVICE OUTPUT.iso [SECTORS]
#
#   Linux : read-disc.sh /dev/sr0 disc.iso
#   macOS : read-disc.sh /dev/rdisk4 disc.iso     (unmount first: diskutil unmountDisk /dev/disk4)
#
# SECTORS defaults to the volume size in the ISO 9660 header (sector 16). A disc without a
# filesystem (a DVD generated without --data-dir) has none: pass SECTORS, or the whole device
# is read until it ends.
#
# The image always starts at LBA 0, which is what `extract` and `cd-extract` expect.
set -u

if [ "$#" -lt 2 ]; then
  sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
fi
device="$1"
output="$2"
sectors="${3:-}"

if [ ! -e "$device" ]; then
  echo "error: $device does not exist" >&2
  exit 2
fi

if [ -z "$sectors" ]; then
  # ISO 9660 primary volume descriptor: "CD001" at byte 1, volume space size (LE) at byte 80.
  header="$(dd if="$device" bs=2048 skip=16 count=1 2>/dev/null | od -An -tu1 -v)"
  # shellcheck disable=SC2086
  set -- $header
  if [ "$#" -ge 84 ] && [ "${2}" = 67 ] && [ "${3}" = 68 ] && [ "${4}" = 48 ] && [ "${5}" = 48 ] && [ "${6}" = 49 ]; then
    sectors=$(( ${81} + ${82} * 256 + ${83} * 65536 + ${84} * 16777216 ))
    echo "volume size from the ISO 9660 header: $sectors sectors"
  else
    echo "no ISO 9660 header: reading until the device ends"
  fi
fi

args=(if="$device" of="$output" bs=2048 conv=noerror,sync)
[ -n "$sectors" ] && args+=(count="$sectors")
if [ "$(uname)" = "Linux" ]; then
  args+=(status=progress)
fi

echo "reading $device -> $output (2048-byte sectors, unreadable sectors become zeros)"
dd "${args[@]}"
status=$?

bytes=$(wc -c < "$output" | tr -d ' ')
echo "wrote $bytes bytes = $(( bytes / 2048 )) sectors"
if [ $(( bytes % 2048 )) -ne 0 ]; then
  echo "warning: the size is not a multiple of 2048" >&2
fi
echo "next: disc-image-studio compare --expected generated.iso --actual $output"
exit $status
