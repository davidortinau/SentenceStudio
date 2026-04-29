# Blazor Read-Only Mode Pattern

**Pattern for making Blazor pages/forms read-only based on entity state.**

## When to Use

- **System-managed entities** — data generated/updated by background processes (smart resources, AI-generated profiles, SRS schedules)
- **Historical records** — completed activities, archived sessions, audit logs
- **Permission-based restrictions** — user viewing another user's data, read-only roles

## Anti-Patterns

❌ **Don't do these:**
- Hiding inputs entirely — loses accessibility and hides existing values
- Client-side-only restrictions — users can bypass via DevTools/API
- Removing navigation/view features — users should be able to read everything

## The Pattern

### 1. Visual State (Disable, Don't Hide)

Use the `disabled` attribute on form inputs:

```razor
<input type="text" class="form-control" @bind="resource.Title" disabled="@resource.IsReadOnly" />
<textarea @bind="resource.Description" disabled="@resource.IsReadOnly"></textarea>
<select @bind="resource.Category" disabled="@resource.IsReadOnly">...</select>
```

**Why disable instead of hide:**
- Screen readers can still announce field label + value
- Sighted users see grayed-out state (clear "read-only" signal)
- User can still copy values with keyboard/mouse

### 2. Hide Mutating Buttons

Remove action buttons that would modify the entity:

```razor
<PageHeader Title="@entity.Name" ShowBack="true" OnBack="GoBack">
    <PrimaryActions>
        @if (!entity.IsReadOnly)
        {
            <button class="btn btn-primary" @onclick="Save">Save</button>
        }
    </PrimaryActions>
    <SecondaryActions>
        @if (!entity.IsReadOnly)
        {
            <li><button class="dropdown-item" @onclick="Save">Save</button></li>
            <li><hr class="dropdown-divider" /></li>
            <li><button class="dropdown-item text-danger" @onclick="Delete">Delete</button></li>
        }
    </SecondaryActions>
</PageHeader>
```

**CRITICAL:** For Blazor `RenderFragment` parameters (like `<PrimaryActions>`), the `@if` goes **inside** the fragment, not wrapped around it:

```razor
❌ WRONG — Compile error "Unrecognized child content":
<PageHeader>
    @if (!entity.IsReadOnly)
    {
        <PrimaryActions>
            <button>Save</button>
        </PrimaryActions>
    }
</PageHeader>

✅ CORRECT:
<PageHeader>
    <PrimaryActions>
        @if (!entity.IsReadOnly)
        {
            <button>Save</button>
        }
    </PrimaryActions>
</PageHeader>
```

### 3. Hide Auxiliary Edit Features

Conditionally hide any UI that adds/removes/generates data:

```razor
@if (!resource.IsReadOnly)
{
    <div class="mb-3">
        <label>Import vocabulary</label>
        <textarea @bind="vocabList"></textarea>
        <button @onclick="ImportVocab">Import</button>
    </div>
}
```

### 4. Keep View-Only Features Working

Always keep these visible/functional:
- Info banners explaining why the page is read-only
- Lists/tables of related data
- Navigation buttons (Back, Cancel, Close)
- Export/Download actions (if applicable)

Example:

```razor
@if (entity.IsReadOnly)
{
    <div class="alert alert-info">
        <i class="bi bi-info-circle"></i>
        <strong>Read Only</strong> — This entity is managed by the system.
    </div>
}

<div class="card">
    <h5>Related Items</h5>
    @if (entity.Items?.Count > 0)
    {
        <ul>
            @foreach (var item in entity.Items)
            {
                <li>@item.Name</li>
            }
        </ul>
    }
</div>
```

### 5. Server-Side Guards (Defense in Depth)

**Never trust client-side disabled state.** Add server-side checks to all mutation handlers:

```csharp
private async Task SaveEntity()
{
    if (entity.IsReadOnly)
    {
        Logger.LogWarning("Attempt to save read-only entity {EntityId} blocked", entity.Id);
        return;
    }

    // ... normal save logic
}

private async Task DeleteEntity()
{
    if (entity.IsReadOnly)
    {
        Logger.LogWarning("Attempt to delete read-only entity {EntityId} blocked", entity.Id);
        return;
    }

    // ... normal delete logic
}

private async Task AddRelatedItem()
{
    if (entity.IsReadOnly)
    {
        Logger.LogWarning("Attempt to add item to read-only entity {EntityId} blocked", entity.Id);
        return;
    }

    // ... normal add logic
}
```

**Why:** Users can bypass disabled inputs via browser DevTools or direct API calls. Server-side guards ensure data integrity even if UI is compromised.

## Complete Example

```razor
@page "/resources/edit/{Id}"
@inject ResourceRepository Repo
@inject ToastService Toast
@inject ILogger<ResourceEdit> Logger

@if (isLoading)
{
    <div class="spinner-border"></div>
}
else
{
    <PageHeader Title="@resource.Title" ShowBack="true" OnBack="GoBack">
        <PrimaryActions>
            @if (!resource.IsSystemManaged)
            {
                <button class="btn btn-primary" @onclick="Save">Save</button>
            }
        </PrimaryActions>
        <SecondaryActions>
            @if (!resource.IsSystemManaged)
            {
                <li><button class="dropdown-item" @onclick="Save">Save</button></li>
                <li><hr class="dropdown-divider" /></li>
                <li><button class="dropdown-item text-danger" @onclick="Delete">Delete</button></li>
            }
        </SecondaryActions>
    </PageHeader>

    @if (resource.IsSystemManaged)
    {
        <div class="alert alert-info">
            <i class="bi bi-robot"></i>
            <strong>System Managed</strong> — This resource is automatically updated.
        </div>
    }

    <div class="card">
        <h5>Basic Information</h5>
        
        <div class="mb-3">
            <label>Title</label>
            <input type="text" class="form-control" 
                   @bind="resource.Title" 
                   disabled="@resource.IsSystemManaged" />
        </div>

        <div class="mb-3">
            <label>Description</label>
            <textarea class="form-control" 
                      @bind="resource.Description" 
                      disabled="@resource.IsSystemManaged"></textarea>
        </div>

        <div class="mb-3">
            <label>Category</label>
            <select class="form-select" 
                    @bind="resource.Category" 
                    disabled="@resource.IsSystemManaged">
                <option>Option 1</option>
                <option>Option 2</option>
            </select>
        </div>
    </div>

    @if (!resource.IsSystemManaged)
    {
        <div class="card">
            <h5>Import Items</h5>
            <textarea @bind="itemList"></textarea>
            <button class="btn btn-secondary" @onclick="ImportItems">Import</button>
        </div>
    }

    <div class="card">
        <h5>Related Items</h5>
        @if (resource.Items?.Count > 0)
        {
            @foreach (var item in resource.Items)
            {
                <div>@item.Name</div>
            }
        }
        else
        {
            <p class="text-muted">No items yet.</p>
        }
    </div>
}

@code {
    [Parameter] public string Id { get; set; } = "";
    
    private Resource resource = new();
    private bool isLoading = true;
    private string itemList = "";

    protected override async Task OnInitializedAsync()
    {
        resource = await Repo.GetResourceAsync(Id);
        isLoading = false;
    }

    private void GoBack() => NavManager.NavigateTo("/resources");

    private async Task Save()
    {
        // Server-side guard
        if (resource.IsSystemManaged)
        {
            Logger.LogWarning("Attempt to save system-managed resource {ResourceId} blocked", resource.Id);
            return;
        }

        try
        {
            await Repo.SaveResourceAsync(resource);
            Toast.ShowSuccess("Saved successfully");
        }
        catch (Exception ex)
        {
            Toast.ShowError($"Save failed: {ex.Message}");
        }
    }

    private async Task Delete()
    {
        // Server-side guard
        if (resource.IsSystemManaged)
        {
            Logger.LogWarning("Attempt to delete system-managed resource {ResourceId} blocked", resource.Id);
            return;
        }

        try
        {
            await Repo.DeleteResourceAsync(resource);
            Toast.ShowSuccess("Deleted successfully");
            GoBack();
        }
        catch (Exception ex)
        {
            Toast.ShowError($"Delete failed: {ex.Message}");
        }
    }

    private async Task ImportItems()
    {
        // Server-side guard
        if (resource.IsSystemManaged)
        {
            Logger.LogWarning("Attempt to import items into system-managed resource {ResourceId} blocked", resource.Id);
            return;
        }

        // ... import logic
    }
}
```

## Testing Checklist

✅ **Read-only entity:**
- [ ] All inputs are disabled (grayed out, not hidden)
- [ ] Save/Delete/Add/Generate buttons are hidden
- [ ] Info banner explains why page is read-only
- [ ] Related data (lists, tables) is visible
- [ ] Navigation works (Back, Cancel)
- [ ] Screen reader announces fields + values

✅ **Editable entity:**
- [ ] All inputs are enabled
- [ ] Save/Delete/Add/Generate buttons are visible and functional
- [ ] No read-only banner shown
- [ ] Normal edit workflow works end-to-end

✅ **Server-side guards:**
- [ ] Attempting mutation via DevTools/API returns early with log warning
- [ ] No database changes occur for read-only entities

## Common Gotchas

1. **RenderFragment conditionals** — `@if` goes **inside** the fragment, not around it
2. **Disabled but not readonly** — Use `disabled`, not `readonly` (readonly allows form submission)
3. **Missing server guards** — Client-side disabled can be bypassed; always guard server-side
4. **Hiding view features** — Don't hide lists/tables of related data; users should be able to view everything

## Real-World Applications

- **ResourceEdit.razor** — Smart resources (DailyReview, NewWords, Struggling, Phrases, Sentences) are system-managed; users can view vocab lists but cannot edit title/transcript/tags
- **SkillProfile pages** — AI-generated skill profiles are read-only; manually-created profiles are editable
- **Activity history** — Completed activities are view-only; in-progress activities can be abandoned/resumed

## See Also

- `.squad/decisions/inbox/kaylee-resourceedit-readonly.md` — Full decision doc for smart resource implementation
- Accessibility: [WCAG 2.1 — Name, Role, Value](https://www.w3.org/WAI/WCAG21/Understanding/name-role-value.html)
