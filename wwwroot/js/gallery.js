// Small progressive enhancements: theme, mobile filters and copy buttons.

const THEME_KEY = 'qm-theme';

function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    try {
        localStorage.setItem(THEME_KEY, theme);
    } catch (e) {
        // Storage can be unavailable in private windows.
    }
    document.cookie = THEME_KEY + '=' + theme + '; path=/; max-age=31536000; samesite=lax';
}

function toggleTheme() {
    const current = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
    applyTheme(current === 'dark' ? 'light' : 'dark');
}

function storedPreference() {
    try {
        const value = localStorage.getItem(THEME_KEY);
        if (value === 'light' || value === 'dark') {
            return value;
        }
    } catch (e) {
        // Storage can be unavailable in private windows.
    }

    const match = document.cookie.match(/(?:^|;\s*)qm-theme=(light|dark)/);
    return match ? match[1] : null;
}

function applyStoredTheme() {
    let theme = storedPreference();

    if (theme !== 'light' && theme !== 'dark') {
        theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    document.documentElement.setAttribute('data-theme', theme);
}

function scrollToHash(id) {
    const current = window.location.pathname + window.location.search;

    if (id.length === 0) {
        window.scrollTo({ top: 0, behavior: 'smooth' });
        window.history.replaceState(null, '', current);
        return true;
    }

    const element = document.getElementById(id);
    if (!element) {
        return false;
    }

    element.scrollIntoView({ behavior: 'smooth', block: 'start' });
    // The document has <base href="/">, so a bare "#id" would resolve to /#id.
    window.history.replaceState(null, '', current + '#' + id);
    return true;
}

document.addEventListener('error', (event) => {
    const image = event.target;
    if (image instanceof HTMLImageElement && image.hasAttribute('data-avatar')) {
        image.remove();
    }
}, true);

// Capture phase: Blazor's enhanced navigation also listens for anchors, and a
// bare "#section" link would otherwise be treated as a route change to "/".
document.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }

    const hashLink = target.closest('a[href^="#"]');
    if (hashLink) {
        event.preventDefault();
        event.stopPropagation();
        scrollToHash((hashLink.getAttribute('href') || '#').slice(1));
    }

    const scrollButton = target.closest('[data-scroll-to]');
    if (scrollButton) {
        event.preventDefault();
        event.stopPropagation();
        scrollToHash(scrollButton.getAttribute('data-scroll-to') || '');
    }
}, true);

function closeFilters() {
    document.body.classList.remove('filters-open');
}

document.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }

    if (target.closest('[data-theme-toggle]')) {
        toggleTheme();
        return;
    }

    if (target.closest('[data-filters-toggle]')) {
        document.body.classList.toggle('filters-open');
        return;
    }

    if (target.closest('[data-filters-close]') || target.classList.contains('filters-scrim')) {
        closeFilters();
        return;
    }

    const copyButton = target.closest('[data-copy]');
    if (copyButton) {
        const value = copyButton.getAttribute('data-copy') || '';
        const original = copyButton.innerHTML;
        navigator.clipboard.writeText(value).then(() => {
            copyButton.classList.add('is-copied');
            copyButton.innerHTML = '<svg class="icon" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg> Copied';
            window.setTimeout(() => {
                copyButton.classList.remove('is-copied');
                copyButton.innerHTML = original;
            }, 1600);
        }).catch(() => {});
    }
});

document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
        closeFilters();
    }
});

window.qmScrollToResults = function () {
    const element = document.getElementById('results');
    if (element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
};

function syncTitle() {
    const explicit = document.querySelector('[data-page-title]')?.getAttribute('data-page-title');
    const h1 = document.querySelector('main h1, main .empty-state h2');
    const text = (explicit || h1?.textContent || '').trim().replace(/\s+/g, ' ');
    if (!text) {
        return;
    }
    document.title = text.includes('QM Gallery') ? text : text + ' · QM Gallery';
}

window.qmSyncTitle = syncTitle;

applyStoredTheme();
syncTitle();

if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
    window.Blazor.addEventListener('enhancedload', () => {
        applyStoredTheme();
        window.setTimeout(syncTitle, 0);
    });
}

// Keep the client preference authoritative even if a server-rendered
// navigation swaps in a different data-theme value.
new MutationObserver(() => {
    const current = document.documentElement.getAttribute('data-theme');
    const preferred = storedPreference();
    if (preferred && current !== preferred) {
        document.documentElement.setAttribute('data-theme', preferred);
    }
}).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
