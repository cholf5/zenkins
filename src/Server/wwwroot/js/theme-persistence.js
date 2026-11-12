/**
 * Bridges BlazorBlueprint's localStorage theme state and a server-readable cookie.
 *
 * The server renders <html class="dark"> from the `zenkins-theme` cookie, so a saved theme
 * needs no client-side script to avoid a flash. This script keeps the two stores in sync:
 * on load it seeds `bb-theme` (what ThemeService reads) from the cookie, and a MutationObserver
 * writes the cookie back whenever the active theme changes.
 *
 * Loaded as a classic, blocking script in <head>: no Node.js, no build step, no inline script.
 */
(function () {
    'use strict';

    var COOKIE_NAME = 'zenkins-theme';
    var STORAGE_KEY = 'bb-theme';
    var ONE_YEAR_SECONDS = 60 * 60 * 24 * 365;

    function readCookie() {
        var match = document.cookie.match(/(?:^|;\s*)zenkins-theme=(dark|light)(?:;|$)/);
        return match ? match[1] : null;
    }

    function writeCookie(isDark) {
        document.cookie = COOKIE_NAME + '=' + (isDark ? 'dark' : 'light') +
            '; path=/; max-age=' + ONE_YEAR_SECONDS + '; SameSite=Lax';
    }

    var saved = readCookie();
    if (!saved) {
        // First run after introducing the cookie, or a browser that kept localStorage but lost
        // the cookie: adopt the existing BlazorBlueprint choice before the first paint.
        try {
            var legacy = JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null');
            if (legacy && typeof legacy.isDarkMode === 'boolean') {
                saved = legacy.isDarkMode ? 'dark' : 'light';
                writeCookie(legacy.isDarkMode);
            }
        } catch (e) {
            // Ignore a malformed legacy value; the cookie stays the source of truth.
        }
    }

    if (saved) {
        // The server already rendered the matching class. Mirror the value into the format
        // ThemeService expects so its first interactive render does not disagree with the DOM.
        try {
            var raw = localStorage.getItem(STORAGE_KEY);
            var state = raw ? JSON.parse(raw) : {};
            if (!state || typeof state !== 'object') {
                state = {};
            }
            state.isDarkMode = saved === 'dark';
            localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
        } catch (e) {
            // localStorage can be blocked or full; the cookie still drives the next page load.
        }

        document.documentElement.classList.toggle('dark', saved === 'dark');
    }

    new MutationObserver(function () {
        writeCookie(document.documentElement.classList.contains('dark'));
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
})();
