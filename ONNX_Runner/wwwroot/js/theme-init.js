// Applies the saved/system theme before the main module boots to avoid a visual flash.
(function() {
    const savedTheme = localStorage.getItem('tsubaki-theme');
    const systemPrefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    const currentTheme = savedTheme || (systemPrefersDark ? 'dark' : 'light');
    document.documentElement.setAttribute('data-theme', currentTheme);
})();