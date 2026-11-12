/**
 * Resolves the theme before the first paint and keeps the three stores in agreement.
 *
 * The server renders <html class="dark"> from the `zenkins-theme` cookie, so a saved theme
 * needs no client-side script to avoid a flash. This script covers what the server cannot see:
 *
 *   1. a cookie exists — the server already rendered the matching class; mirror it into
 *      BlazorBlueprint's `bb-theme` entry so ThemeService's first interactive render agrees.
 *   2. only a legacy `bb-theme` entry exists — adopt it and write the cookie.
 *   3. neither exists — resolve the OS preference here, then set the class, write the cookie
 *      and seed `bb-theme`. Without this the server renders light, `theme.js` picks dark from
 *      `prefers-color-scheme`, and the whole page flips after hydration.
 *
 * After the first run the cookie always exists, so case 3 happens once per browser rather than
 * on every visit, and the cascade of authority stays cookie → localStorage → OS preference.
 *
 * Loaded as a classic, blocking script in <head>: no Node.js, no build step, no inline script.
 * The cookie name is emitted by App.razor from `ThemePreference.CookieName`, so it is not
 * repeated here.
 */
(function () {
    'use strict';

    // Without the data attribute there is no reliable cookie name to use. Stay inert rather than
    // guess one that could silently drift from the C# constant; App.razor always emits it.
    var script = document.currentScript;
    var COOKIE_NAME = script ? script.getAttribute('data-theme-cookie') : null;
    if (!COOKIE_NAME) {
        return;
    }

    var STORAGE_KEY = 'bb-theme';
    var ONE_YEAR_SECONDS = 60 * 60 * 24 * 365;
    var root = document.documentElement;

    function readCookie() {
        var parts = document.cookie ? document.cookie.split(';') : [];
        for (var i = 0; i < parts.length; i++) {
            var pair = parts[i].trim().split('=');
            if (pair[0] === COOKIE_NAME && (pair[1] === 'dark' || pair[1] === 'light')) {
                return pair[1];
            }
        }
        return null;
    }

    function writeCookie(isDark) {
        document.cookie = COOKIE_NAME + '=' + (isDark ? 'dark' : 'light') +
            '; path=/; max-age=' + ONE_YEAR_SECONDS + '; SameSite=Lax';
    }

    function readStoredTheme() {
        try {
            var raw = localStorage.getItem(STORAGE_KEY);
            var state = raw ? JSON.parse(raw) : null;
            return state && typeof state.isDarkMode === 'boolean' ? state : null;
        } catch (e) {
            // Blocked or malformed localStorage. Fall through to the OS preference.
            return null;
        }
    }

    function seedStoredTheme(isDark) {
        try {
            var raw = localStorage.getItem(STORAGE_KEY);
            var state = raw ? JSON.parse(raw) : null;
            if (!state || typeof state !== 'object') {
                state = {};
            }
            // Preserve base colour, radius and design fields ThemeService may have saved.
            state.isDarkMode = isDark;
            localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
        } catch (e) {
            // localStorage can be blocked or full; the cookie still drives the next page load.
        }
    }

    function prefersDark() {
        // Deterministic light when the media query is missing or throws (older engines, kiosks,
        // privacy modes) instead of leaving the class undecided.
        try {
            return typeof window !== 'undefined' &&
                typeof window.matchMedia === 'function' &&
                window.matchMedia('(prefers-color-scheme: dark)').matches === true;
        } catch (e) {
            return false;
        }
    }

    var saved = readCookie();
    if (!saved) {
        var stored = readStoredTheme();
        if (stored) {
            saved = stored.isDarkMode ? 'dark' : 'light';
        }
    }

    var isDark = saved ? saved === 'dark' : prefersDark();

    root.classList.toggle('dark', isDark);
    writeCookie(isDark);
    seedStoredTheme(isDark);

    // Keep the cookie in sync when ThemeService changes the theme later in this session.
    new MutationObserver(function () {
        writeCookie(root.classList.contains('dark'));
    }).observe(root, { attributes: true, attributeFilter: ['class'] });
})();
