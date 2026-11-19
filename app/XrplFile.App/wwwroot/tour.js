// Spotlight support for the tutorial overlay (TutorialOverlay.razor). This file only measures and
// isolates; where the hole and the bubble go is decided in C# (TutorialPlacement).
window.xfTour = (function () {
    // Elements this script set `inert` on, so release() can undo exactly that and nothing the page
    // had set for its own reasons.
    let isolated = [];
    let dotnet = null;
    let pending = false;
    let observer = null;

    function boxOf(element) {
        const box = element.getBoundingClientRect();
        return box.width > 0 && box.height > 0 ? box : null;
    }

    function schedule() {
        // Scroll and mutation events arrive in bursts; one callback per animation frame is enough
        // and keeps the .NET round trip off the hot path.
        if (pending || !dotnet) return;
        pending = true;
        window.requestAnimationFrame(function () {
            pending = false;
            if (dotnet) dotnet.invokeMethodAsync('OnLayoutChanged').catch(function () { });
        });
    }

    function onKeyDown(event) {
        if (event.key === 'Escape' && dotnet) {
            dotnet.invokeMethodAsync('OnEscape').catch(function () { });
        }
    }

    return {
        // For each name, the first element carrying data-tour=name with a non-empty box. The first
        // hit overall is scrolled into view before anything is measured, so the rectangles are the
        // ones the person will see. Names with no visible element are simply absent: the History
        // region sits on the rail and on the tab bar and only one of them is laid out at a time.
        measure: function (names, bubble) {
            const found = [];
            for (const name of names) {
                for (const element of document.querySelectorAll('[data-tour="' + name + '"]')) {
                    if (boxOf(element)) {
                        found.push(element);
                        break;
                    }
                }
            }

            if (found.length > 0) {
                found[0].scrollIntoView({ block: 'nearest', inline: 'nearest' });
            }

            const rects = found.map(function (element) {
                const box = element.getBoundingClientRect();
                return { x: box.x, y: box.y, width: box.width, height: box.height };
            });
            const bubbleBox = bubble ? bubble.getBoundingClientRect() : { width: 0, height: 0 };

            return {
                rects: rects,
                viewWidth: document.documentElement.clientWidth,
                viewHeight: document.documentElement.clientHeight,
                bubbleWidth: bubbleBox.width,
                bubbleHeight: bubbleBox.height
            };
        },

        // Makes everything except the overlay and the usable elements unreachable by pointer,
        // keyboard and assistive technology. Inert goes on the *siblings* along each path up to
        // <body>, never on the path itself: an inert ancestor would take the overlay or the usable
        // control down with it.
        isolate: function (keepSelectors) {
            window.xfTour.release();

            const keep = new Set();
            const root = document.getElementById('xf-tour');
            const starts = root ? [root] : [];
            for (const selector of keepSelectors) {
                for (const element of document.querySelectorAll(selector)) starts.push(element);
            }

            for (const start of starts) {
                for (let node = start; node && node !== document.body; node = node.parentElement) {
                    keep.add(node);
                }
            }

            for (const node of keep) {
                const parent = node.parentElement;
                if (!parent) continue;
                for (const sibling of parent.children) {
                    if (keep.has(sibling) || sibling.inert || sibling.id === 'blazor-error-ui') continue;
                    sibling.inert = true;
                    isolated.push(sibling);
                }
            }
        },

        release: function () {
            for (const element of isolated) element.inert = false;
            isolated = [];
        },

        watch: function (target) {
            window.xfTour.unwatch();
            dotnet = target;
            window.addEventListener('resize', schedule);
            // Capture phase: scroll does not bubble, and the scrolling box is some inner element.
            window.addEventListener('scroll', schedule, true);
            document.addEventListener('keydown', onKeyDown);

            const content = document.getElementById('xf-content');
            if (content) {
                // `inert` is deliberately not in the filter: isolate() sets it and would feed itself.
                observer = new MutationObserver(schedule);
                observer.observe(content, {
                    childList: true,
                    subtree: true,
                    attributes: true,
                    attributeFilter: ['class', 'style', 'disabled', 'hidden', 'aria-expanded']
                });
            }
        },

        unwatch: function () {
            window.removeEventListener('resize', schedule);
            window.removeEventListener('scroll', schedule, true);
            document.removeEventListener('keydown', onKeyDown);
            if (observer) observer.disconnect();
            observer = null;
            dotnet = null;
            pending = false;
        }
    };
})();
