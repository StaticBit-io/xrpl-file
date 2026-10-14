// Phone-width More menu and focus housekeeping for Shell.razor (design 2026-10-13, sections 3–4).
// The layout itself is pure CSS; this file only reacts when the window crosses the 720 px line.
window.xfShell = {
    watchWidth: function (dotnet) {
        window.xfShell.dotnet = dotnet;
        const wide = window.matchMedia('(min-width: 720px)');
        wide.addEventListener('change', function () {
            if (wide.matches) {
                dotnet.invokeMethodAsync('OnWideLayout').catch(function () { });
            }

            // The navigation that held focus may just have become display:none; hand focus to the
            // content region instead of leaving it on nothing.
            const active = document.activeElement;
            if (active && active !== document.body && active.offsetParent === null) {
                const content = document.getElementById('xf-content');
                if (content) {
                    content.focus();
                }
            }
        });

        // The engine can drop focus from a control that a resize just hid before the change event
        // above runs, so that handler sees <body> and does nothing; catch that from the focusout side.
        if (!window.xfShell.focusGuard) {
            window.xfShell.focusGuard = true;
            document.addEventListener('focusout', function (event) {
                const lost = event.target;
                if (event.relatedTarget !== null || !lost.closest || !lost.closest('.xf-rail, .xf-tabbar, .xf-topbar')) {
                    return;
                }

                window.setTimeout(function () {
                    if (!lost.isConnected) {
                        return;
                    }

                    const active = document.activeElement;
                    const content = document.getElementById('xf-content');
                    if (content && (!active || active === document.body) && lost.getClientRects().length === 0) {
                        content.focus();
                    }
                }, 0);
            });
        }

        // Escape closes the More panel wherever focus is (design section 4), so it is heard on the document;
        // whether it means anything is the model's call, this only reports the key while the panel is in the DOM.
        if (!window.xfShell.escapeGuard) {
            window.xfShell.escapeGuard = true;
            document.addEventListener('keydown', function (event) {
                if (event.key === 'Escape' && window.xfShell.dotnet && document.getElementById('xf-more')) {
                    window.xfShell.dotnet.invokeMethodAsync('OnEscape').catch(function () { });
                }
            });
        }
    }
};
