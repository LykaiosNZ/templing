// SourcePicker (a MudAutocomplete): Escape while its list is open closes just the list. MudSelect stops the key itself,
// but MudAutocomplete lets it bubble on to the dialog around it (closing the dialog and leaving the list on screen) or
// to the page's shortcuts (Stop on the Metronome). Caught here on the field's wrapper, before either sees it, and
// handed to .NET to close the list; with the list closed, Escape goes on as usual.
export function guardEscape(el, dotnet) {
    const onKey = e => {
        if (e.key !== 'Escape' || !el.querySelector('input[aria-expanded="true"]')) return;
        e.stopPropagation();
        e.preventDefault();
        dotnet.invokeMethodAsync('CloseList');
    };
    el.addEventListener('keydown', onKey);
    return { dispose: () => el.removeEventListener('keydown', onKey) };
}
