// F-021: the project parameters editor. The same shape as js/freestyle-steps.js: a plain HTML form
// posting to the real endpoint, with this script owning the dynamic row list (add / remove) and
// serializing the rows into the one hidden ParametersJson field the endpoint parses. The server
// (ProjectParameters.BuildDefinitionsJson) validates every rule - the browser only collects values.
(function () {
    "use strict";

    var FORM_SELECTOR = "form[data-parameter-rows-form]";
    var ROW_SELECTOR = "[data-parameter-row]";

    function rows(form) {
        return Array.prototype.slice.call(form.querySelectorAll(ROW_SELECTOR));
    }

    function fieldValue(row, name) {
        var field = row.querySelector('[data-parameter-field="' + name + '"]');
        return field ? field.value : "";
    }

    function serialize(form) {
        var drafts = rows(form).map(function (row) {
            return {
                name: fieldValue(row, "name"),
                type: fieldValue(row, "type"),
                defaultValue: fieldValue(row, "default"),
                choicesText: fieldValue(row, "choices"),
                description: fieldValue(row, "description"),
            };
        });

        var target = form.querySelector("#parameters-json");
        if (target) {
            target.value = JSON.stringify(drafts);
        }
    }

    function renumber(form) {
        var all = rows(form);
        for (var i = 0; i < all.length; i++) {
            var label = all[i].querySelector("[data-parameter-row-index]");
            if (label) {
                label.textContent = String(i + 1);
            }
        }
    }

    // B-010: both handlers live on `document` for the same reason js/freestyle-steps.js does - the
    // script loads once per document, while a Blazor navigation replaces this form's nodes. Deriving
    // the form from the event means a row added a moment ago is handled by the same code.
    document.addEventListener("submit", function (event) {
        var form = event.target && event.target.closest ? event.target.closest(FORM_SELECTOR) : null;
        if (form) {
            serialize(form);
        }
    }, true);

    document.addEventListener("click", function (event) {
        var button = event.target.closest ? event.target.closest("[data-parameter-action]") : null;
        if (!button) {
            return;
        }

        var form = button.closest(FORM_SELECTOR);
        if (!form) {
            return;
        }

        switch (button.getAttribute("data-parameter-action")) {
            case "add":
                var template = form.querySelector("#parameter-row-template");
                var list = form.querySelector("[data-parameter-rows-list]");
                if (template && list) {
                    list.appendChild(template.content.firstElementChild.cloneNode(true));
                    renumber(form);
                }
                break;
            case "remove":
                var row = button.closest(ROW_SELECTOR);
                if (row) {
                    row.remove();
                    renumber(form);
                }
                break;
        }

        event.preventDefault();
    });
})();
