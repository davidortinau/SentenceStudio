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
 * Destroy a Tom Select instance.
 * @param {string} elementId - Select element ID
 */
export function destroyTomSelect(elementId) {
    if (tomSelects[elementId]) {
        tomSelects[elementId].destroy();
        delete tomSelects[elementId];
    }
}
