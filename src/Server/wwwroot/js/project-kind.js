// F-020/F-022: the project form's selectors. A Freestyle project has no repository, no default branch
// and no pipeline path; a Pipeline project has a definition source, and an inline one has a script
// instead of the repository fields.
//
// B-010: every half of the form is rendered by the server in every state, and `hidden` + `disabled`
// carry whether it applies - the server renders the state it knows, this script keeps it true while
// the reader switches. The hidden half's inputs are disabled so the browser's own `required`
// validation cannot block the submission and the inputs submit nothing. The server-side validation
// (ProjectService) remains the authority; this is form hygiene, exactly like js/antiforgery.js.
//
// B-010: the listeners live on `document`, never on the controls themselves. Blazor navigations and
// re-renders replace the form's DOM without re-running this file (it is a plain <script> in App.razor,
// loaded once per document), so a handler bound to a control at load time is bound to a node that may
// no longer exist - which is how a Freestyle choice used to leave the repository fields on screen.
(function () {
    "use strict";

    var REPOSITORY_FIELDS_SELECTOR = "[data-project-repository-fields]";
    var INLINE_FIELDS_SELECTOR = "[data-project-inline-fields]";
    var SOURCE_FIELDS_SELECTOR = "[data-project-source-fields]";
    var KIND_CHOICE_SELECTOR = "input[type=radio][data-project-kind-choice]";
    var SOURCE_CHOICE_SELECTOR = "input[type=radio][data-project-source-choice]";
    var FREESTYLE_VALUE = "freestyle";
    var INLINE_VALUE = "inline";

    function setEnabled(root, enabled) {
        if (!root) {
            return;
        }

        root.hidden = !enabled;
        var inputs = root.querySelectorAll("input, select, textarea");
        for (var i = 0; i < inputs.length; i++) {
            inputs[i].disabled = !enabled;
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

        setEnabled(sourceFields, !isFreestyle);
        setEnabled(repositoryFields, !isFreestyle && !isInline);
        setEnabled(inlineFields, !isFreestyle && isInline);
    }

    document.addEventListener("change", function (event) {
        var target = event.target;
        if (!target || !target.matches) {
            return;
        }

        if (target.matches(KIND_CHOICE_SELECTOR) || target.matches(SOURCE_CHOICE_SELECTOR)) {
            apply();
        }
    });

    // The browser's own arrival at the form: a full page load, and (for the statically rendered pages
    // that use it) an enhanced navigation that patched a new form into this document.
    apply();
    document.addEventListener("enhancedload", apply);
})();
