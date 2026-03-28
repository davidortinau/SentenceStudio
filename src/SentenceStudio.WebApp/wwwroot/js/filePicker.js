// File picker interop for server-side Blazor
window.filePickerInterop = {
    pickFile: function (acceptTypes) {
        return new Promise(function (resolve) {
            var input = document.createElement('input');
            input.type = 'file';
            if (acceptTypes) {
                input.accept = acceptTypes;
            }

            var resolved = false;
            function complete(value) {
                if (!resolved) {
                    resolved = true;
                    resolve(value);
                }
            }

            input.addEventListener('change', function () {
                if (!input.files || input.files.length === 0) {
                    complete(null);
                    return;
                }

                var file = input.files[0];
                var reader = new FileReader();
                reader.onload = function () {
                    var bytes = new Uint8Array(reader.result);
                    complete({ fileName: file.name, content: Array.from(bytes) });
                };
                reader.onerror = function () {
                    console.error('[filePickerInterop] FileReader error:', reader.error);
                    complete(null);
                };
                reader.readAsArrayBuffer(file);
            });

            // Detect cancel: when the window regains focus after the dialog, if no
            // file was selected the change event never fires.
            function onFocusBack() {
                window.removeEventListener('focus', onFocusBack);
                // Small delay — the change event fires slightly after focus returns.
                setTimeout(function () { complete(null); }, 300);
            }
            window.addEventListener('focus', onFocusBack);

            input.click();
        });
    }
};
