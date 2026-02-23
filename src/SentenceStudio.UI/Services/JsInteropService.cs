using Microsoft.JSInterop;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// JS interop for Chart.js and Tom Select.
/// Audio, speech, and file operations use native C# services directly via DI.
/// </summary>
public class JsInteropService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference _module;

    public JsInteropService(IJSRuntime js)
    {
        _js = js;
    }

    private async Task<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/app.js");
        return _module;
    }

    public async Task CreateDoughnutChartAsync(string canvasId, string[] labels, double[] values, string[] colors)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("createDoughnutChart", canvasId, labels, values, colors);
    }

    public async Task UpdateChartDataAsync(string canvasId, double[] values)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("updateChartData", canvasId, values);
    }

    public async Task InitTomSelectAsync(string elementId, object[] options, bool multiple = false)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("initTomSelect", elementId, options, multiple);
    }

    public async Task<string[]> GetTomSelectValuesAsync(string elementId)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string[]>("getTomSelectValues", elementId);
    }

    public async Task DestroyTomSelectAsync(string elementId)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("destroyTomSelect", elementId);
    }

    public async Task CopyToClipboardAsync(string text)
    {
        await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            await _module.DisposeAsync();
    }
}
