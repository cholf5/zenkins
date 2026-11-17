// F-020/F-022: the project form's selectors. A Freestyle project has no repository, no default branch
// and no pipeline path; a Pipeline project has a definition source, and an inline one has a script
// instead of the repository fields. The hidden half's inputs are disabled so the browser's own
// `required` validation cannot block the submission and the inputs submit nothing. The server-side
// validation (ProjectService) remains the authority; this is form hygiene, exactly like
// js/antiforgery.js.
(function () {
    "use strict";

    var REPOSITORY_FIELDS_SELECTOR = "[data-project-repository-fields]";
    var INLINE_FIELDS_SELECTOR = "[data-project-inline-fields]";
    var SOURCE_FIELDS_SELECTOR = "[data-project-source-fields]";
    var KIND_CHOICE_SELECTOR = "input[type=radio][data-project-kind-choice]";
    var SOURCE_CHOICE_SELECTOR = "input[type=radio][data-project-source-choice]";
    var FREESTYLE_VALUE = "freestyle";
    var INLINE_VALUE = "inline";

    function disableAll(root, disabled) {
        if (!root) {
            return;
        }

        root.hidden = disabled;
        var inputs = root.querySelectorAll("input, select, textarea");
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = disabled;
        }
    }

    function apply() {
        var kindChoice = document.querySelector(KIND_CHOICE_SELECTOR + ":checked");
        var sourceChoice = document.querySelector(SOURCE_CHOICE_SELECTOR + ":checked");
        var sourceFields = document.querySelector(SOURCE_FIELDS_SELECTOR);
        var repositoryFields = document.querySelector(REPOSITORY_FIELDS_SELECTOR);
        var inlineFields = document.querySelector(INLINE_FIELDS_SELECTOR);

        // No kind selector (the kind-locked edit form): the server rendered exactly the half that
        // project owns, so there is nothing to toggle.
        if (!kindChoice) {
            return;
        }

        var isFreestyle = kindChoice.value === FREESTYLE_VALUE;
        var isInline = !isFreestyle && sourceChoice !== null && sourceChoice.value === INLINE_VALUE;

        disableAll(sourceFields, isFreestyle);
        disableAll(repositoryFields, isFreestyle || isInline);
        disableAll(inlineFields, isFreestyle || !isInline);
    }

    function init() {
        var kindRadios = document.querySelectorAll(KIND_CHOICE_SELECTOR);
        var sourceRadios = document.querySelectorAll(SOURCE_CHOICE_SELECTOR);
        if (kindRadios.length === 0 && sourceRadios.length === 0) {
            return;
        }

        apply();
        var i;
        for (i = 0; i < kindRadios.length; i++) {
            kindRadios[i].addEventListener("change", apply);
        }

        for (i = 0; i < sourceRadios.length; i++) {
            sourceRadios[i].addEventListener("change", apply);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
