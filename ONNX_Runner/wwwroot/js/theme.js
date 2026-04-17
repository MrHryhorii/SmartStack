export function initTheme() {
    const htmlEl = document.documentElement;
    const themeBtn = document.getElementById('themeToggle');

    // Check localStorage or OS preference
    const savedTheme = localStorage.getItem('tsubaki-theme');
    const systemPrefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;

    // Apply the theme
    const currentTheme = savedTheme || (systemPrefersDark ? 'dark' : 'light');
    htmlEl.setAttribute('data-theme', currentTheme);

    // Toggle button logic
    themeBtn.addEventListener('click', () => {
        const newTheme = htmlEl.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
        htmlEl.setAttribute('data-theme', newTheme);
        localStorage.setItem('tsubaki-theme', newTheme);
    });
}