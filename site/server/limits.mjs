/**
 * A counter of things running at once. `tryAcquire` returns a release function, or null when `max`
 * are already running; releasing twice counts once.
 */
export function createLimiter(max) {
  let active = 0;
  return {
    tryAcquire() {
      if (active >= max) return null;
      active++;
      let released = false;
      return () => {
        if (released) return;
        released = true;
        active--;
      };
    },
    get active() {
      return active;
    },
  };
}
