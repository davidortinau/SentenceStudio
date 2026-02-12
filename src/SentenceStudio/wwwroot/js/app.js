// SentenceStudio Blazor JS Interop Module
// Chart.js and Tom Select integration

const charts = {};
const tomSelects = {};

/**
 * Create a Chart.js doughnut chart.
 * @param {string} canvasId - Canvas element ID
 * @param {string[]} labels - Data labels
 * @param {number[]} values - Data values
 * @param {string[]} colors - Background colors
 */
export function createDoughnutChart(canvasId, labels, values, colors) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    // Destroy existing chart if any
    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(canvas, {
        type: 'doughnut',
        data: {
            labels: labels,
            datasets: [{
                data: values,
                backgroundColor: colors,
                borderWidth: 0,
                hoverOffset: 4
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            cutout: '65%',
            plugins: {
                legend: {
                    position: 'bottom',
                    labels: {
                        color: getComputedStyle(document.documentElement)
                            .getPropertyValue('--ss-text-secondary').trim() || '#C6D0E7',
                        padding: 12,
                        usePointStyle: true,
                        pointStyleWidth: 10
                    }
                }
            }
        }
    });
}

/**
 * Update chart data.
 * @param {string} canvasId - Canvas element ID
 * @param {number[]} values - New data values
 */
export function updateChartData(canvasId, values) {
    if (charts[canvasId]) {
        charts[canvasId].data.datasets[0].data = values;
        charts[canvasId].update();
    }
}

/**
 * Initialize a Tom Select combobox with optional Blazor callback.
 * @param {string} elementId - Select element ID
 * @param {object[]} options - Options array [{value, text}]
 * @param {boolean} multiple - Allow multiple selection
 * @param {object} dotNetRef - Optional DotNet object reference for change callback
 * @param {string} callbackMethod - Optional method name to invoke on change
 */
export function initTomSelect(elementId, options, multiple, dotNetRef, callbackMethod) {
    const el = document.getElementById(elementId);
    if (!el) return;

    // Destroy existing instance
    if (tomSelects[elementId]) {
        tomSelects[elementId].destroy();
    }

    tomSelects[elementId] = new TomSelect(el, {
        options: options,
        maxItems: multiple ? null : 1,
        plugins: multiple ? ['remove_button'] : [],
        create: false,
        allowEmptyOption: true
    });

    if (dotNetRef && callbackMethod) {
        tomSelects[elementId].on('change', function() {
            const val = tomSelects[elementId].getValue();
            const values = Array.isArray(val) ? val : (val ? [val] : []);
            dotNetRef.invokeMethodAsync(callbackMethod, values);
        });
    }
}

/**
 * Get selected values from Tom Select.
 * @param {string} elementId - Select element ID
 * @returns {string[]} Selected values
 */
export function getTomSelectValues(elementId) {
    if (tomSelects[elementId]) {
        const val = tomSelects[elementId].getValue();
        return Array.isArray(val) ? val : [val];
    }
    return [];
}

/**
 * Set selected values on a Tom Select instance.
 * @param {string} elementId - Select element ID
 * @param {string[]} values - Values to select
 */
export function setTomSelectValues(elementId, values) {
    if (tomSelects[elementId]) {
        tomSelects[elementId].clear(true);
        if (values && values.length > 0) {
            values.forEach(v => tomSelects[elementId].addItem(v, true));
        }
    }
}

/**
 * Destroy a Tom Select instance.
 * @param {string} elementId - Select element ID
 */
export function destroyTomSelect(elementId) {
    if (tomSelects[elementId]) {
        tomSelects[elementId].destroy();
        delete tomSelects[elementId];
    }
}

/**
 * Apply theme to the HTML element.
 * Sets both data-bs-theme (light/dark) and data-ss-theme (color palette).
 * @param {string} theme - Theme name (seoul-pop, ocean, forest, sunset, monochrome)
 * @param {string} mode - Mode (light or dark)
 */
const BOOTSTRAP_CDN = 'https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css';
const BOOTSWATCH_THEMES = ['flatly', 'sketchy', 'slate', 'vapor', 'brite'];

export function applyTheme(theme, mode) {
    const html = document.documentElement;
    html.setAttribute('data-bs-theme', mode);
    html.setAttribute('data-ss-theme', theme);

    // Swap Bootstrap CSS for Bootswatch themes
    const link = document.getElementById('bootstrap-theme');
    if (link) {
        if (BOOTSWATCH_THEMES.includes(theme)) {
            link.href = `css/themes/${theme}.min.css`;
        } else {
            link.href = BOOTSTRAP_CDN;
        }
    }
}

export function setFontScale(scale) {
    document.documentElement.style.setProperty('--ss-font-scale', scale);
}

export function resetScroll() {
    const main = document.querySelector('.main-content');
    if (main) main.scrollTop = 0;
}

