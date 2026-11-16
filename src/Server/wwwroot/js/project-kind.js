// F-020: the project form's kind selector toggles the repository fields. A Freestyle project has no
// repository, no default branch and no pipeline path - the three inputs are hidden and disabled while
// that radio is selected, so the browser's own `required` validation cannot block a Freestyle create
// and the inputs submit nothing. The server-side validation (ProjectService) remains the authority;
// this is form hygiene, exactly like js/antiforgery.js.
(function () {
    "use strict";

    var REPOSITORY_FIELDS_SELECTOR = "[data-project-repository-fields]";
    var CHOICE_SELECTOR = "input[type=radio][data-project-kind-choice]";
    var FREESTYLE_VALUE = "freestyle";

    function apply() {
        var fields = document.querySelector(REPOSITORY_FIELDS_SELECTOR);
        var checked = document.querySelector(CHOICE_SELECTOR + ":checked");
        if (!fields || !checked) {
            // No selector rendered (the kind-locked edit form) or no fields: nothing to toggle, and
            // the form stays exactly as the server rendered it.
            return;
        }

        var isFreestyle = checked.value === FREESTYLE_VALUE;
        fields.hidden = isFreestyle;

        // Disabled inputs are exempt from the browser's validation and submit no value.
        var inputs = fields.querySelectorAll("input, select, textarea");
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = isFreestyle;
        }
    }

    function init() {
        var radios = document.querySelectorAll(CHOICE_SELECTOR);
        if (radios.length === 0) {
            return;
        }

        apply();
        for (var i = 0; i < radios.length; i++) {
            radios[i].addEventListener("change", apply);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
