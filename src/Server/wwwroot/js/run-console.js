// Card F-047: the console follows the tail until the reader scrolls up.
//
// Blazor owns the lines and the pause state; this file owns only the scroll position and the fact
// that the reader's finger is on the wheel. init() attaches one listener per console element,
// afterRender() is called after every render (scroll to the bottom when following, or to the current
// search match), and a programmatic scroll is marked `suppress` so its own scroll event cannot
// bounce back into the page and turn following off.
(() => {
  "use strict";

  const consoles = new Map();

  function stateFor(id) {
    let state = consoles.get(id);
    if (!state) {
      state = { el: null, dotNet: null, handler: null, suppress: false, scheduled: false };
      consoles.set(id, state);
    }
    return state;
  }

  function atBottom(el) {
    return el.scrollHeight - el.scrollTop - el.clientHeight <= 24;
  }

  function notify(id) {
    const state = consoles.get(id);
    if (!state || state.suppress || !state.el || !state.dotNet || state.scheduled) {
      return;
    }

    state.scheduled = true;
    requestAnimationFrame(() => {
      state.scheduled = false;
      if (state.suppress || !state.el || !state.dotNet) {
        return;
      }

      state.dotNet.invokeMethodAsync("OnConsoleScroll", atBottom(state.el)).catch(() => {
        // A circuit that went away while the wheel was still moving is not an error worth logging.
      });
    });
  }

  function scrollToBottom(state) {
    state.suppress = true;
    state.el.scrollTop = state.el.scrollHeight;
    requestAnimationFrame(() => {
      state.suppress = false;
    });
  }

  function scrollToMatch(state, seq) {
    const target = state.el.querySelector('[data-seq="' + seq + '"]');
    if (!target) {
      return;
    }

    state.suppress = true;
    target.scrollIntoView({ block: "center" });
    requestAnimationFrame(() => {
      state.suppress = false;
    });
  }

  function init(id, dotNet) {
    const el = document.getElementById(id);
    if (!el) {
      return;
    }

    const state = stateFor(id);
    if (state.el && state.el !== el && state.handler) {
      state.el.removeEventListener("scroll", state.handler);
    }

    state.el = el;
    state.dotNet = dotNet;
    if (!state.handler) {
      state.handler = () => notify(id);
    }

    el.addEventListener("scroll", state.handler, { passive: true });
  }

  function afterRender(id, paused, matchSeq) {
    const state = consoles.get(id);
    if (!state || !state.el) {
      return;
    }

    if (matchSeq !== null && matchSeq !== undefined && matchSeq !== 0) {
      scrollToMatch(state, matchSeq);
      return;
    }

    if (!paused) {
      scrollToBottom(state);
    }
  }

  function dispose(id) {
    const state = consoles.get(id);
    if (!state) {
      return;
    }

    if (state.el && state.handler) {
      state.el.removeEventListener("scroll", state.handler);
    }

    consoles.delete(id);
  }

  window.zenkinsConsole = { init, afterRender, dispose };
})();
