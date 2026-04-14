# Deploy Runbook

## "Publish" = Azure + DX24

When the Captain says **"publish"**, **"deploy"**, or **"push to my phone"**, execute BOTH steps below. They are not optional. Both targets must point at the **same Azure API**.

---

## Step 1: Deploy to Azure

```bash
# Ensure VPN is OFF (management.azure.com times out on VPN)
cd /Users/davidortinau/work/SentenceStudio
azd deploy
```

**Expected output:** All services succeed (api, webapp, cache, db, marketing, workers).  
**Webapp URL:** `https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io/`  
**API URL:** `https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/`

---

## Step 2: Build & Deploy iOS to DX24

DX24 is Captain's iPhone 15 Pro. Device ID: `CF4F94E3-A1C9-5617-A089-9ABB0110A09F`

### 2a. Switch to .NET 11 Preview 3 SDK (required for Xcode 26.3)

```bash
cd /Users/davidortinau/work/SentenceStudio
cp global.json global.json.bak
cat > global.json << 'EOF'
{
  "sdk": {
    "version": "11.0.100-preview.3.26209.122",
    "rollForward": "latestFeature",
    "allowPrerelease": true
  }
}
EOF
```

### 2b. Build Release with Azure API URL

```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64
```

### 2c. Install and launch on DX24

```bash
xcrun devicectl device install app \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  src/SentenceStudio.iOS/bin/Release/net10.0-ios/ios-arm64/SentenceStudio.iOS.app

xcrun devicectl device process launch \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  com.simplyprofound.sentencestudio
```

### 2d. Restore global.json

```bash
cp global.json.bak global.json && rm global.json.bak
```

---

## Local Development Build (NOT for publish)

For local Aspire development only. Points at localhost, requires Aspire running.

```bash
# Debug build — uses appsettings.json (localhost:5081)
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net10.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64
```

---

## Common Issues

| Problem | Fix |
|---------|-----|
| `azd deploy` times out | Turn off VPN, retry |
| Xcode version mismatch (26.2 vs 26.3) | Use .NET 11 Preview 3 SDK (step 2a) |
| Device locked error on install | Unlock DX24, retry |
| LOCAL ribbon on phone | Built with Debug config — rebuild with Release + env var (step 2b) |
| Phone app can't reach API | Missing `services__api__https__0` env var at build time |
