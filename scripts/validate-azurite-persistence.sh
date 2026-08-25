#!/usr/bin/env bash
set -euo pipefail
# ---------------------------------------------------------------------------
# validate-azurite-persistence.sh
#
# Guards against a RECURRING cross-process data-loss bug: if the Aspire AppHost
# configures Azure Storage's Azurite emulator without a stable named data
# volume AND persistent container lifetime, every AppHost restart regenerates
# the Data Protection key ring and all previously-protected coach content
# becomes permanently unreadable.
#
# This check is STATIC, DETERMINISTIC, and DEVICE-FREE. It greps AppHost.cs
# for the required RunAsEmulator configuration. It belongs in CI alongside
# validate-migration-attributes.sh.
#
# Earning event: 2026-08-17 — coach conversation payloads unprotectable after
# AppHost restart because Azurite used an anonymous Docker volume.
# ---------------------------------------------------------------------------

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPHOST="$ROOT/src/SentenceStudio.AppHost/AppHost.cs"

fail=0

if [ ! -f "$APPHOST" ]; then
  echo "FAIL: AppHost.cs not found at $APPHOST"
  exit 1
fi

# Check 1: WithDataVolume must appear inside RunAsEmulator for storage
if ! grep -q 'RunAsEmulator.*WithDataVolume\|WithDataVolume.*RunAsEmulator' "$APPHOST" && \
   ! grep -A5 'AddAzureStorage.*storage' "$APPHOST" | grep -q 'WithDataVolume'; then
  echo "FAIL: Azurite storage emulator is missing WithDataVolume() — data will not survive container restarts."
  fail=1
fi

# Check 2: WithLifetime(ContainerLifetime.Persistent) must be present for storage
if ! grep -A5 'AddAzureStorage.*storage' "$APPHOST" | grep -q 'WithLifetime.*Persistent'; then
  echo "FAIL: Azurite storage emulator is missing WithLifetime(ContainerLifetime.Persistent) — container will be recreated on restart."
  fail=1
fi

# Check 3: Volume name must be a stable named volume (not empty/anonymous)
if grep -A5 'AddAzureStorage.*storage' "$APPHOST" | grep -q 'WithDataVolume()'; then
  echo "FAIL: Azurite WithDataVolume() has no explicit volume name — will use anonymous volume."
  fail=1
fi

if [ $fail -ne 0 ]; then
  echo ""
  echo "The Azurite emulator MUST be configured with a stable named volume and persistent"
  echo "lifetime to preserve the Data Protection key ring across AppHost restarts. Example:"
  echo ""
  echo '  .RunAsEmulator(azurite => azurite'
  echo '      .WithDataVolume("sentencestudio-local-azurite-data")'
  echo '      .WithLifetime(ContainerLifetime.Persistent))'
  echo ""
  exit 1
fi

echo "OK: Azurite storage emulator has stable named volume + persistent lifetime."
