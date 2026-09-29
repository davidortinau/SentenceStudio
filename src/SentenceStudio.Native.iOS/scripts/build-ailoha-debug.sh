#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" != 1 ]]; then
  printf 'Usage: %s <official-AilohaAgent.xcframework>\n' "$0" >&2
  exit 2
fi

head_dir="$(cd "$(dirname "$0")/.." && pwd)"
official="$1"
if [[ ! -f "$official/Info.plist" ]]; then
  echo 'Official AilohaAgent XCFramework not found.' >&2
  exit 1
fi

build_root="$head_dir/obj/ailoha-debug"
mkdir -p "$build_root/device/AilohaDebugShim.framework" \
  "$build_root/simulator/AilohaDebugShim.framework" \
  "$build_root/cache" "$build_root/scratch"
device_sdk="$(xcrun --sdk iphoneos --show-sdk-path)"
sim_sdk="$(xcrun --sdk iphonesimulator --show-sdk-path)"
source_file="$head_dir/Native/AilohaDebugShim.swift"
plist_source="$head_dir/Native/AilohaDebugShim.plist"

build_slice() {
  local name="$1" sdk="$2" target="$3" framework_suffix="$4"
  local framework="$build_root/$name/AilohaDebugShim.framework"
  SDKROOT="$sdk" TMPDIR="$build_root/scratch" xcrun swiftc \
    -emit-library -swift-version 5 -module-name AilohaDebugShim \
    -module-cache-path "$build_root/cache" \
    -target "$target" -sdk "$sdk" \
    -F "$official/$framework_suffix" -framework AilohaAgent \
    -Xlinker -install_name \
    -Xlinker '@rpath/AilohaDebugShim.framework/AilohaDebugShim' \
    -Xlinker -rpath -Xlinker '@loader_path/..' \
    -o "$framework/AilohaDebugShim" "$source_file"
  cp "$plist_source" "$framework/Info.plist"
}

build_slice device "$device_sdk" arm64-apple-ios26.0 ios-arm64
build_slice simulator "$sim_sdk" arm64-apple-ios26.0-simulator ios-arm64_x86_64-simulator

output="$build_root/AilohaDebugShim.xcframework"
if [[ -e "$output" ]]; then
  echo "Refusing to overwrite an existing Debug shim: $output" >&2
  exit 1
fi
TMPDIR="$build_root/scratch" xcodebuild -create-xcframework \
  -framework "$build_root/device/AilohaDebugShim.framework" \
  -framework "$build_root/simulator/AilohaDebugShim.framework" \
  -output "$output"
printf '%s\n' "$output"
