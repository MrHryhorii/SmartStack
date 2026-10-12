// Friendly dashboard labels for the eight device classes; server metadata controls availability.
const PROFILE_LABELS = Object.freeze({
    'wearable-class-device': 'Wearable',
    'handset-class-device': 'Phone Speaker',
    'headphone-class-device': 'Headphones',
    'small-bluetooth-speaker-class-device': 'Small Bluetooth Speaker',
    'medium-bluetooth-speaker-class-device': 'Medium Bluetooth Speaker',
    'large-home-entertainment-class-device': 'Home Entertainment',
    'large-automotive-class-device': 'Car Speakers',
    'telephony-class-application': 'Telephone Line'
});

// Builds unchecked profile rows, enabling only the device classes reported by the server.
export function initEqualizerProfiles(list, availableIds) {
    const available = new Set((Array.isArray(availableIds) ? availableIds : [])
        .filter(id => typeof id === 'string')
        .map(id => id.trim().toLowerCase()));
    let enabledCount = 0;

    const rows = Object.entries(PROFILE_LABELS).map(([id, name]) => {
        const enabled = available.has(id);
        if (enabled) enabledCount++;

        const row = document.createElement('li');
        row.className = 'equalizer-profile';
        const label = document.createElement('label');
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.value = id;
        checkbox.disabled = !enabled;
        const text = document.createElement('span');
        text.className = 'equalizer-label';
        const title = document.createElement('span');
        title.textContent = name;
        const identifier = document.createElement('code');
        identifier.className = 'equalizer-id';
        identifier.textContent = id;
        text.append(title, identifier);
        label.append(checkbox, text);

        const controls = document.createElement('div');
        controls.className = 'equalizer-moves';
        for (const [direction, arrow] of [['up', '\u2191'], ['down', '\u2193']]) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'equalizer-move';
            button.dataset.move = direction;
            button.textContent = arrow;
            button.disabled = !enabled;
            button.setAttribute('aria-label', `Move ${name} ${direction}`);
            button.title = `Move ${name} ${direction}; wraps at the end of the list`;
            controls.append(button);
        }
        row.append(label, controls);
        return row;
    });
    list.replaceChildren(...rows);
    list.addEventListener('click', moveProfile);
    return enabledCount;
}

// Moves the existing row one step, wrapping at either edge and retaining keyboard focus.
function moveProfile(event) {
    const button = event.target.closest?.('button[data-move]');
    if (!button || button.disabled) return;
    const row = button.closest('.equalizer-profile');
    const list = event.currentTarget;
    if (!row || row.parentElement !== list) return;

    if (button.dataset.move === 'up') {
        const previous = row.previousElementSibling;
        if (previous) list.insertBefore(row, previous);
        else list.append(row);
    } else {
        const next = row.nextElementSibling;
        if (next) list.insertBefore(next, row);
        else list.prepend(row);
    }
    button.focus();
}

// Returns checked, available profile IDs in their current top-to-bottom display order.
export function selectedEqualizers(list) {
    return Array.from(list.querySelectorAll('input[type="checkbox"]:checked:not(:disabled)'),
        checkbox => checkbox.value);
}
