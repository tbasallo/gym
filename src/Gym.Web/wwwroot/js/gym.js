// Small browser helpers called from Blazor via JS interop.
window.gym = (function () {
    // Follow the OS light/dark setting. Re-applied after Blazor's enhanced navigation,
    // which replaces the <html> attributes with the server's.
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const applyTheme = () => document.documentElement.setAttribute('data-bs-theme', media.matches ? 'dark' : 'light');
    applyTheme();
    media.addEventListener('change', applyTheme);
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', applyTheme);
    }

    let wakeLock = null;

    async function requestWakeLock() {
        try {
            if ('wakeLock' in navigator) {
                wakeLock = await navigator.wakeLock.request('screen');
            }
        } catch {
            wakeLock = null;
        }
    }

    document.addEventListener('visibilitychange', () => {
        if (wakeLock !== null && document.visibilityState === 'visible') {
            requestWakeLock();
        }
    });

    return {
        timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone || '',

        // Keeps the phone screen on during a workout.
        keepAwake: async (on) => {
            if (on) {
                await requestWakeLock();
            } else if (wakeLock) {
                try { await wakeLock.release(); } catch { }
                wakeLock = null;
            }
        },

        copy: async (text) => {
            try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
        },

        buzz: () => {
            try { if (navigator.vibrate) { navigator.vibrate([200, 100, 200]); } } catch { }
        },

        scrollIntoView: (id) => {
            const el = document.getElementById(id);
            if (el) { el.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
        }
    };
})();
