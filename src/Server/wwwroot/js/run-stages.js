// F-030: a failed run's page opens at the first failed step. The markup carries the marker on exactly
// one step row (data-run-first-failed) because the stage headers alone cannot express "the first one that
// failed" - the server already knows it, so the script does not compute anything, it scrolls.
//
// Two entry points on purpose: a full page load fires DOMContentLoaded once, and Blazor's enhanced
// navigation (the default for internal links such as "All runs" -> a run) swaps the page without a new
// document, firing "enhancedload" instead. A reader who arrived with their own fragment (#run-log, or a
// step link) keeps their destination: the page does not override an explicit jump.
(function () {
    "use strict";

    function jumpToFirstFailedStep() {
        if (window.location.hash) {
            return;
        }

        var target = document.querySelector("[data-run-first-failed]");
        if (!target || typeof target.scrollIntoView !== "function") {
            return;
        }

        target.scrollIntoView({ block: "center" });
    }

    document.addEventListener("enhancedload", jumpToFirstFailedStep);

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", jumpToFirstFailedStep);
    } else {
        jumpToFirstFailedStep();
    }
})();
