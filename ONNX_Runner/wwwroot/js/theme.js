export function initTheme() {
    const htmlEl = document.documentElement;
    const themeBtn = document.getElementById('themeToggle');

    // Persists manual theme changes.
    themeBtn.addEventListener('click', () => {
        const newTheme = htmlEl.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
        htmlEl.setAttribute('data-theme', newTheme);
        localStorage.setItem('tsubaki-theme', newTheme);
    });
}