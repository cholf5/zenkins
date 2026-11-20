// F-021: the project parameters editor. The same shape as js/freestyle-steps.js: a plain HTML form posting
// to the real endpoint, with this script owning the dynamic row list (add / remove) and serializing the rows
// into the one hidden ParametersJson field the endpoint parses. The server
// (ProjectParameters.BuildDefinitionsJson) validates every rule - the browser only collects values.
//
// Since F-045 the rows are lines in a list + inspector (js/split-list.js): this file supplies the rows'
// words (F-045's summary), tells the frame a row appeared, and reads the fields through the pointer the frame
// leaves on a row whose block it has moved into the pane. Reading the row directly would find no inputs for
// the selected parameter and save it as empty.
(function () {
    "use strict";

    var FORM_SELECTOR = "form[data-parameter-rows-form]";
    var ROW_SELECTOR = "[data-parameter-row]";
    var EDITOR_KEY = "parameters";

    function rows(form) {
        return Array.prototype.slice.call(form.querySelectorAll(ROW_SELECTOR));
    }

    // The fields may have been moved into the inspector pane by js/split-list.js, which leaves the row a
    // pointer to them. Without this line the selected parameter saves every field as empty.
    function fieldScope(row) {
        return row._splitDetail || row;
    }

    function fieldValue(row, name) {
        var field = fieldScope(row).querySelector('[data-parameter-field="' + name + '"]');
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

    // What a collapsed row says: the parameter's name (or the editor's word for one that has none yet) and its
    // type. The words come from the editor's data-split-labels, so neither language lives in this file.
    function parameterSummary(row, ctx) {
        var name = (ctx.field("name") || "").trim();
        var type = (ctx.field("type") || "").trim();
        return {
            title: name || ctx.label("unnamed"),
            chips: type ? [type] : [],
        };
    }

    function splitList() {
        return window.zenkinsSplit || null;
    }

    function installSummary() {
        var api = splitList();
        if (api) {
            api.summarize(EDITOR_KEY, parameterSummary);
        }
    }

    // A row list changed: the frame has to see the new row (its block is still inside it, not in the pane), and
    // opening it is what lands the reader in the fields they just created.
    function afterAdd(form) {
        var api = splitList();
        if (!api) {
            return;
        }

        api.register(form);
        var all = rows(form);
        if (all.length > 0) {
            api.select(all[all.length - 1]);
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
                // B-011: same trap as js/freestyle-steps.js - Blazor's renderer appends the template's row to
                // the element with appendChild, so after the circuit activates the row is no longer in
                // template.content and "Add parameter" threw on a null row and did nothing.
                var source = template ? (template.content.firstElementChild || template.firstElementChild) : null;
                if (source && list) {
                    list.appendChild(source.cloneNode(true));
                    renumber(form);
                    afterAdd(form);
                }
                break;
            case "remove":
                var row = button.closest(ROW_SELECTOR);
                if (row) {
                    if (row.parentNode) {
                        row.parentNode.removeChild(row);
                    }

                    renumber(form);

                    // The frame re-opens a row that still exists; a removed row's block must not linger in the
                    // pane, and re-registering is what moves the selection off the row that is gone.
                    var api = splitList();
                    if (api) {
                        api.register(form);
                    }
                }
                break;
        }

        event.preventDefault();
    });

    installSummary();

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", installSummary);
    }

    // Load order between this file and js/split-list.js must not matter, and a re-rendered page must get its
    // lines back: summarizing is idempotent, so both hooks are safe to repeat.
    document.addEventListener("enhancedload", installSummary);
})();
