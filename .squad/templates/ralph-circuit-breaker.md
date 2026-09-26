# Ralph Circuit Breaker — GPT-Only Rate Limit Fallback

> Classic circuit breaker pattern (Hystrix / Polly / Resilience4j) applied to Copilot model selection.
> When the preferred GPT model hits rate limits, Ralph tries other high-quality GPT models, then self-heals.

## Problem

When running multiple Ralph instances across repos, a preferred model's rate limit can affect every instance. SentenceStudio uses the same GPT-only, maximum-quality policy for Ralph as for all other agents. Prefer `gpt-6-sol`; check the current platform catalog before attempting any fallback.

Concurrent workers can exhaust the preferred model's quota together, so every retry must select an explicit GPT that supports `max` reasoning and `long_context`.

## Circuit Breaker States

```
┌─────────┐   rate limit error    ┌────────┐
│ CLOSED  │ ───────────────────►  │  OPEN  │
│ (normal)│                       │(fallback)│
└────┬────┘   ◄──────────────── └────┬────┘
     │        2 consecutive          │
     │        successes              │ cooldown expires
     │                               ▼
     │                          ┌──────────┐
     └───── success ◄────────  │HALF-OPEN │
             (close)            │ (testing) │
                                └──────────┘
```

### CLOSED (normal operation)
- Use preferred model from config
- Every successful response confirms circuit stays closed
- On rate limit error → transition to OPEN

### OPEN (rate limited — fallback active)
- Fall back through the strongest compatible GPT chain:
  1. `gpt-6-astra`
  2. `gpt-6-luna`
  3. `gpt-5.6-sol`
  4. `gpt-5.6-terra`
- If no compatible GPT is available, stop and report the rate limit; never omit the model parameter or switch providers.
- Start cooldown timer (default: 10 minutes)
- When cooldown expires → transition to HALF-OPEN

### HALF-OPEN (testing recovery)
- Try preferred model again
- If 2 consecutive successes → transition to CLOSED
- If rate limit error → back to OPEN, reset cooldown

## State File: `.squad/ralph-circuit-breaker.json`

```json
{
  "state": "closed",
  "preferredModel": "gpt-6-sol",
  "fallbackChain": ["gpt-6-astra", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra"],
  "currentFallbackIndex": 0,
  "cooldownMinutes": 10,
  "openedAt": null,
  "halfOpenSuccesses": 0,
  "consecutiveFailures": 0,
  "metrics": {
    "totalFallbacks": 0,
    "totalRecoveries": 0,
    "lastFallbackAt": null,
    "lastRecoveryAt": null
  }
}
```

## PowerShell Functions

Paste these into your `ralph-watch.ps1` or source them from a shared module.

### Allowed GPT Catalog and State Migration

Run this validation whenever persisted circuit-breaker state is loaded. It removes unsupported provider IDs before model selection and persists the repaired state.

```powershell
$script:AllowedGptModels = @(
    "gpt-6-sol",
    "gpt-6-astra",
    "gpt-6-luna",
    "gpt-5.6-sol",
    "gpt-5.6-terra"
)

function Test-AllowedGptModel {
    param([AllowNull()][string]$Model)

    return $null -ne $Model -and $script:AllowedGptModels -ccontains $Model
}

function New-DefaultCircuitBreakerState {
    return [pscustomobject]@{
        state                = "closed"
        preferredModel       = "gpt-6-sol"
        fallbackChain        = @("gpt-6-astra", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra")
        currentFallbackIndex = 0
        cooldownMinutes      = 10
        openedAt             = $null
        halfOpenSuccesses    = 0
        consecutiveFailures  = 0
        metrics              = @{
            totalFallbacks = 0
            totalRecoveries = 0
            lastFallbackAt = $null
            lastRecoveryAt = $null
        }
    }
}

function ConvertTo-ValidatedCircuitBreakerState {
    param([object]$State)

    if ($null -eq $State) {
        return [pscustomobject]@{ State = (New-DefaultCircuitBreakerState); Migrated = $true }
    }

    $migrated = $false
    $defaults = New-DefaultCircuitBreakerState
    foreach ($propertyName in @("preferredModel", "fallbackChain", "currentFallbackIndex")) {
        if ($null -eq $State.PSObject.Properties[$propertyName]) {
            $defaultValue = $defaults.PSObject.Properties[$propertyName].Value
            if ($defaultValue -is [System.Array]) {
                $defaultValue = @($defaultValue)
            }
            $State | Add-Member -NotePropertyName $propertyName -NotePropertyValue $defaultValue
            $migrated = $true
        }
    }

    if (-not (Test-AllowedGptModel $State.preferredModel)) {
        $State.preferredModel = "gpt-6-sol"
        $migrated = $true
    }

    $configuredFallbacks = @($State.fallbackChain)
    $validatedFallbacks = @(
        $configuredFallbacks | Where-Object {
            $_ -is [string] -and (Test-AllowedGptModel $_)
        } | Select-Object -Unique
    )
    if ($configuredFallbacks.Count -ne $validatedFallbacks.Count) {
        $migrated = $true
    }
    if ($validatedFallbacks.Count -eq 0) {
        $validatedFallbacks = @("gpt-6-astra", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra")
        $migrated = $true
    }
    $State.fallbackChain = $validatedFallbacks

    $fallbackIndex = 0
    try { $fallbackIndex = [int]$State.currentFallbackIndex } catch { $migrated = $true }
    if ($fallbackIndex -lt 0 -or $fallbackIndex -ge $validatedFallbacks.Count) {
        $fallbackIndex = 0
        $migrated = $true
    }
    $State.currentFallbackIndex = $fallbackIndex

    return [pscustomobject]@{ State = $State; Migrated = $migrated }
}
```

### `Get-CircuitBreakerState`

```powershell
function Get-CircuitBreakerState {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    if (-not (Test-Path $StateFile)) {
        $cb = New-DefaultCircuitBreakerState
        Save-CircuitBreakerState -State $cb -StateFile $StateFile
        return $cb
    }

    try {
        $persisted = Get-Content $StateFile -Raw | ConvertFrom-Json
    } catch {
        $persisted = New-DefaultCircuitBreakerState
        Save-CircuitBreakerState -State $persisted -StateFile $StateFile
        return $persisted
    }

    $validated = ConvertTo-ValidatedCircuitBreakerState -State $persisted
    if ($validated.Migrated) {
        Save-CircuitBreakerState -State $validated.State -StateFile $StateFile
        Write-Host "  [circuit-breaker] Migrated persisted model state to the allowed GPT catalog." -ForegroundColor Yellow
    }
    return $validated.State
}
```

### `Save-CircuitBreakerState`

```powershell
function Save-CircuitBreakerState {
    param(
        [object]$State,
        [string]$StateFile = ".squad/ralph-circuit-breaker.json"
    )

    $State | ConvertTo-Json -Depth 3 | Set-Content $StateFile
}
```

### `Get-CurrentModel`

Returns the model Ralph should use right now, based on circuit state.

```powershell
function Get-CurrentModel {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    $cb = Get-CircuitBreakerState -StateFile $StateFile
    if (-not $cb.fallbackChain -or $cb.preferredModel -notmatch '^gpt-[a-z0-9.-]+$' -or @($cb.fallbackChain | Where-Object { $_ -notmatch '^gpt-[a-z0-9.-]+$' }).Count -gt 0) {
        throw "Ralph requires GPT-only preferred and fallback models."
    }
    if ($cb.state -notin @("closed", "open", "half-open")) {
        throw "Unknown Ralph circuit-breaker state: $($cb.state)"
    }

    $model = switch ($cb.state) {
        "closed" {
            $cb.preferredModel
            break
        }
        "open" {
            # Check if cooldown has expired
            if ($cb.openedAt) {
                $opened = [DateTime]::Parse($cb.openedAt)
                $elapsed = (Get-Date) - $opened
                if ($elapsed.TotalMinutes -ge $cb.cooldownMinutes) {
                    # Transition to half-open
                    $cb.state = "half-open"
                    $cb.halfOpenSuccesses = 0
                    Save-CircuitBreakerState -State $cb -StateFile $StateFile
                    Write-Host "  [circuit-breaker] Cooldown expired. Testing preferred model..." -ForegroundColor Yellow
                    $cb.preferredModel
                    break
                }
            }
            # Still in cooldown — use fallback
            if ($cb.currentFallbackIndex -lt 0 -or $cb.currentFallbackIndex -ge $cb.fallbackChain.Count) {
                throw "Ralph fallback index is out of range."
            }
            $idx = $cb.currentFallbackIndex
            $cb.fallbackChain[$idx]
            break
        }
        "half-open" {
            $cb.preferredModel
            break
        }
        default {
            throw "Circuit breaker state '$($cb.state)' is invalid; refusing to select a model."
        }
    }

    if (-not (Test-AllowedGptModel $model)) {
        throw "Circuit breaker refused to return a model outside the allowed GPT catalog."
    }
    return $model
}
```

### `Update-CircuitBreakerOnSuccess`

Call after every successful model response.

```powershell
function Update-CircuitBreakerOnSuccess {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    $cb = Get-CircuitBreakerState -StateFile $StateFile
    $cb.consecutiveFailures = 0

    if ($cb.state -eq "half-open") {
        $cb.halfOpenSuccesses++
        if ($cb.halfOpenSuccesses -ge 2) {
            # Recovery! Close the circuit
            $cb.state = "closed"
            $cb.openedAt = $null
            $cb.halfOpenSuccesses = 0
            $cb.currentFallbackIndex = 0
            $cb.metrics.totalRecoveries++
            $cb.metrics.lastRecoveryAt = (Get-Date).ToString("o")
            Save-CircuitBreakerState -State $cb -StateFile $StateFile
            Write-Host "  [circuit-breaker] RECOVERED — back to preferred model ($($cb.preferredModel))" -ForegroundColor Green
            return
        }
        Save-CircuitBreakerState -State $cb -StateFile $StateFile
        Write-Host "  [circuit-breaker] Half-open success $($cb.halfOpenSuccesses)/2" -ForegroundColor Yellow
        return
    }

    # closed state — nothing to do
}
```

### `Update-CircuitBreakerOnRateLimit`

Call when a model response indicates rate limiting (HTTP 429 or error message containing "rate limit").

```powershell
function Update-CircuitBreakerOnRateLimit {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    $cb = Get-CircuitBreakerState -StateFile $StateFile
    $cb.consecutiveFailures++

    if ($cb.state -eq "closed" -or $cb.state -eq "half-open") {
        # Open the circuit
        $cb.state = "open"
        $cb.openedAt = (Get-Date).ToString("o")
        $cb.halfOpenSuccesses = 0
        $cb.currentFallbackIndex = 0
        $cb.metrics.totalFallbacks++
        $cb.metrics.lastFallbackAt = (Get-Date).ToString("o")
        Save-CircuitBreakerState -State $cb -StateFile $StateFile

        $fallbackModel = $cb.fallbackChain[0]
        Write-Host "  [circuit-breaker] RATE LIMITED — falling back to $fallbackModel (cooldown: $($cb.cooldownMinutes)m)" -ForegroundColor Red
        return
    }

    if ($cb.state -eq "open") {
        # Already open — try next fallback in chain if current one also fails
        if ($cb.currentFallbackIndex -lt ($cb.fallbackChain.Count - 1)) {
            $cb.currentFallbackIndex++
            $nextModel = $cb.fallbackChain[$cb.currentFallbackIndex]
            Write-Host "  [circuit-breaker] Fallback also limited — trying $nextModel" -ForegroundColor Red
        } else {
            Save-CircuitBreakerState -State $cb -StateFile $StateFile
            throw "No compatible GPT fallback remains; stop Ralph rather than choosing a platform default."
        }
        # Reset cooldown timer
        $cb.openedAt = (Get-Date).ToString("o")
        Save-CircuitBreakerState -State $cb -StateFile $StateFile
    }
}
```

## Integration with ralph-watch.ps1

The installed `squad watch --execute` reads `watch.copilotFlags` from `.squad/config.json`; this project's configuration passes `--agent squad --model gpt-6-sol --reasoning-effort max --context long_context --allow-all-tools`. A CLI `--copilot-flags` override replaces that setting and MUST supply all five flags. If Copilot rejects the selected GPT or either capability, stop instead of retrying without them. When using a custom `ralph-watch.ps1` polling loop, wrap the model selection:

```powershell
# Verify this GPT supports max reasoning and long_context before dispatch.
# Never start a session on an implicit model or capability tier.
$result = & copilot -C $repo --agent squad --model gpt-6-sol --reasoning-effort max --context long_context -p $prompt --allow-all-tools 2>&1
if ($LASTEXITCODE -ne 0 -and ($result -join "`n") -notmatch "rate.?limit|429|quota|Too Many Requests") {
    throw "Ralph launch failed; do not retry with platform defaults."
}

# After the call
if (($result -join "`n") -match "rate.?limit|429|quota|Too Many Requests") {
    Update-CircuitBreakerOnRateLimit
} else {
    Update-CircuitBreakerOnSuccess
}
```

### Full integration example

```powershell
# Define or import the validated circuit-breaker functions above before this loop.

while ($true) {
    Write-Host "Polling with model: gpt-6-sol"

    try {
        # The selected GPT must support max reasoning and long_context.
        $response = & copilot -C $repo --agent squad --model gpt-6-sol --reasoning-effort max --context long_context -p $prompt --allow-all-tools 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Ralph session failed: $($response -join "`n")"
        }

        Update-CircuitBreakerOnSuccess
    }
    catch {
        if ($_.Exception.Message -match "rate.?limit|429|quota|Too Many Requests") {
            Update-CircuitBreakerOnRateLimit
            # Wait before retrying the same explicit GPT; never downgrade quality.
            Start-Sleep -Seconds $pollInterval
            continue
        }
        # Other errors — handle normally
        throw
    }

    Start-Sleep -Seconds $pollInterval
}
```

## Configuration

Override defaults by editing `.squad/ralph-circuit-breaker.json`:

| Field | Default | Description |
|-------|---------|-------------|
| `preferredModel` | `gpt-6-sol` | Explicit GPT to use when circuit is closed |
| `fallbackChain` | `["gpt-6-astra", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra"]` | Ordered compatible GPT fallbacks; verify `max` and `long_context` before dispatch |
| `cooldownMinutes` | `10` | How long to wait before testing recovery |

`Get-CircuitBreakerState` validates these persisted model fields on every load. Unsupported values are never selected: an invalid preferred model becomes `gpt-6-sol`, unsupported fallback entries are discarded, and an empty validated chain becomes the GPT-only default chain. Before launch, verify the selected GPT still supports `max` reasoning and `long_context` in the current session; otherwise stop.

## Metrics

The state file tracks operational metrics:

- **totalFallbacks** — How many times the circuit opened
- **totalRecoveries** — How many times it recovered to preferred model
- **lastFallbackAt** — ISO timestamp of last rate limit event
- **lastRecoveryAt** — ISO timestamp of last successful recovery

Query metrics with:
```powershell
$cb = Get-Content .squad/ralph-circuit-breaker.json | ConvertFrom-Json
Write-Host "Fallbacks: $($cb.metrics.totalFallbacks) | Recoveries: $($cb.metrics.totalRecoveries)"
```
