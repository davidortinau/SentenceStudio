# Ralph Circuit Breaker — Model Rate Limit Fallback

> Classic circuit breaker pattern (Hystrix / Polly / Resilience4j) applied to Copilot model selection.
> When the preferred model hits rate limits, Ralph automatically switches to explicit OpenAI GPT fallbacks, then self-heals.

## Problem

When running multiple Ralph instances across repos, Copilot model rate limits cause cascading failures.
All Ralphs fail simultaneously when the preferred model (for example, `gpt-5.6-sol`) hits quota.

Concurrent workers can exhaust the preferred model's quota together, so every retry must select an explicit GPT model.

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
- Fall back through the fast GPT model chain:
  1. `gpt-5.4-mini`
  2. `gpt-5-mini`
- If every fallback is unavailable, stop and surface the model availability failure
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
  "preferredModel": "gpt-5.6-sol",
  "fallbackChain": ["gpt-5.4-mini", "gpt-5-mini"],
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
    "gpt-5.6-sol",
    "gpt-5.6-terra",
    "gpt-5.6-luna",
    "gpt-5.5",
    "gpt-5.4",
    "gpt-5.4-mini",
    "gpt-5.3-codex",
    "gpt-5-mini"
)

function Test-AllowedGptModel {
    param([AllowNull()][string]$Model)

    return $null -ne $Model -and $script:AllowedGptModels -ccontains $Model
}

function New-DefaultCircuitBreakerState {
    return [pscustomobject]@{
        state                = "closed"
        preferredModel       = "gpt-5.6-sol"
        fallbackChain        = @("gpt-5.4-mini", "gpt-5-mini")
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
        $State.preferredModel = "gpt-5.6-sol"
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
        $validatedFallbacks = @("gpt-5.4-mini", "gpt-5-mini")
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
            $idx = [Math]::Min($cb.currentFallbackIndex, $cb.fallbackChain.Count - 1)
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
            throw "All configured OpenAI GPT fallback models are unavailable."
        }
        # Reset cooldown timer
        $cb.openedAt = (Get-Date).ToString("o")
        Save-CircuitBreakerState -State $cb -StateFile $StateFile
    }
}
```

## Integration with ralph-watch.ps1

In your Ralph polling loop, wrap the model selection:

```powershell
# At the top of your polling loop
$model = Get-CurrentModel

# When invoking copilot CLI
$result = copilot-cli --model $model ...

# After the call
if ($result -match "rate.?limit" -or $LASTEXITCODE -eq 429) {
    Update-CircuitBreakerOnRateLimit
} else {
    Update-CircuitBreakerOnSuccess
}
```

### Full integration example

```powershell
# Source the circuit breaker functions
. .squad-templates/ralph-circuit-breaker-functions.ps1

while ($true) {
    $model = Get-CurrentModel
    Write-Host "Polling with model: $model"

    try {
        # Your existing Ralph logic here, but pass $model
        $response = Invoke-RalphCycle -Model $model

        # Success path
        Update-CircuitBreakerOnSuccess
    }
    catch {
        if ($_.Exception.Message -match "rate.?limit|429|quota|Too Many Requests") {
            Update-CircuitBreakerOnRateLimit
            # Retry immediately with fallback model
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
| `preferredModel` | `gpt-5.6-sol` | Model to use when circuit is closed |
| `fallbackChain` | `["gpt-5.4-mini", "gpt-5-mini"]` | Ordered OpenAI GPT fallback models |
| `cooldownMinutes` | `10` | How long to wait before testing recovery |

`Get-CircuitBreakerState` validates these persisted model fields on every load. Unsupported values are never selected: an invalid preferred model becomes `gpt-5.6-sol`, unsupported fallback entries are discarded, and an empty validated chain becomes the GPT-only default chain.

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
