// F-020: the Freestyle steps editor. The form is a plain HTML form posting to the real endpoint
// (antiforgery like every other form); this script is the dynamic row layer the server cannot render
// statelessly: add / remove / reorder a step, then serialize each list into its own hidden field
// (StepsJson and, since F-031, AlwaysStepsJson / SuccessStepsJson / FailureStepsJson). The server
// (FreestyleSteps.BuildSpecJson) rebuilds and re-validates the whole spec from that payload - the
// browser never authors a spec, it only collects field values.
(function () {
    "use strict";

    var FORM_SELECTOR = "form[data-freestyle-steps-form]";
    var LIST_SELECTOR = "[data-freestyle-steps-list]";
    var ROW_SELECTOR = "[data-freestyle-step]";

    function lists(form) {
        return Array.prototype.slice.call(form.querySelectorAll(LIST_SELECTOR));
    }

    function rows(list) {
        return Array.prototype.slice.call(list.querySelectorAll(ROW_SELECTOR));
    }

    function fieldValue(row, name) {
        var field = row.querySelector('[data-freestyle-field="' + name + '"]');
        if (!field) {
            return "";
        }
        return field.type === "checkbox" ? (field.checked ? "true" : "false") : field.value;
    }

    function serializeList(list) {
        return rows(list).map(function (row) {
            return {
                name: fieldValue(row, "name"),
                // F-030: the stage the step renders under. Without this line the field would render, look
                // saved, and never reach the server - the same trap the runsOn line below documents.
                stage: fieldValue(row, "stage"),
                script: fieldValue(row, "script"),
                envLines: fieldValue(row, "env-lines"),
                continueOnError: fieldValue(row, "continue-on-error") === "true",
                artifactsText: fieldValue(row, "artifacts-text"),
                // F-032: the two step knobs travel as the editor's raw text; the server parses and bounds
                // them (a "" is "not declared"), exactly like envLines and artifactsText above.
                timeoutSeconds: fieldValue(row, "timeout-seconds"),
                retry: fieldValue(row, "retry"),
                // F-033: the step's Worker requirement, as the editor's own text. Without this line the value
                // is silently dropped on save - the field would render, look saved, and never reach the server.
                runsOn: fieldValue(row, "runs-on"),
            };
        });
    }

    function serialize(form) {
        var all = lists(form);
        for (var i = 0; i < all.length; i++) {
            var key = all[i].getAttribute("data-freestyle-steps-list");
            var target = form.querySelector('[data-freestyle-json-target="' + key + '"]');
            if (target) {
                target.value = JSON.stringify(serializeList(all[i]));
            }
        }
    }

    function renumber(list) {
        var all = rows(list);
        for (var i = 0; i < all.length; i++) {
            var label = all[i].querySelector("[data-freestyle-step-index]");
            if (label) {
                label.textContent = String(i + 1);
            }
        }
    }

    function addStep(section) {
        var template = section.querySelector("template");
        var list = section.querySelector(LIST_SELECTOR);
        if (!template || !list) {
            return;
        }

        list.appendChild(template.content.firstElementChild.cloneNode(true));
        renumber(list);
    }

    // F-035: append a fully formed row from a `script-step` snippet. The text comes from the page's
    // #snippets-data JSON (the same one the inline/copy affordances read) - the snippet row is never
    // referenced by the saved definition, so a later edit cannot change this project.
    function insertSnippetStep(form, section) {
        var select = form.querySelector("[data-freestyle-snippet-select]");
        var list = section.querySelector(LIST_SELECTOR);
        var template = section.querySelector("template");
        if (!select || !list || !template || select.selectedIndex < 0) {
            return;
        }

        var data = snippetData();
        var snippet = data.scriptStep.filter(function (candidate) {
            return candidate.id === select.options[select.selectedIndex].value;
        })[0];
        if (!snippet) {
            return;
        }

        var row = template.content.firstElementChild.cloneNode(true);
        var name = row.querySelector('[data-freestyle-field="name"]');
        var script = row.querySelector('[data-freestyle-field="script"]');
        if (name) {
            name.value = snippet.name;
        }
        if (script) {
            script.value = snippet.body;
        }

        list.appendChild(row);
        renumber(list);
    }

    function snippetData() {
        var element = document.getElementById("snippets-data");
        if (!element) {
            return { scriptStep: [], luaFragment: [] };
        }

        try {
            return JSON.parse(element.textContent);
        } catch (error) {
            return { scriptStep: [], luaFragment: [] };
        }
    }

    function moveStep(list, row, offset) {
        var all = rows(list);
        var index = all.indexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= all.length) {
            return;
        }

        // insertBefore the neighbour: offset -1 swaps with the previous sibling, +1 inserts before
        // the next-next sibling (or appends at the end).
        var reference = offset < 0 ? all[index - 1] : all[index + 1].nextSibling;
        row.parentNode.insertBefore(row, reference);
        renumber(list);
    }

    // B-010: both handlers live on `document`. This file is a plain <script> in App.razor, loaded once
    // per document, so a handler bound to a form at load time is bound to a node that a later Blazor
    // navigation or re-render has already replaced - the editor would look alive and save nothing.
    // Capture phase for the submit, before js/antiforgery.js fills its token and before anything the
    // page itself might read: the hidden fields must be current whatever the order.
    document.addEventListener("submit", function (event) {
        var form = event.target && event.target.closest ? event.target.closest(FORM_SELECTOR) : null;
        if (form) {
            serialize(form);
        }
    }, true);

    document.addEventListener("click", function (event) {
        var button = event.target.closest ? event.target.closest("[data-freestyle-action]") : null;
        if (!button) {
            return;
        }

        var form = button.closest(FORM_SELECTOR);
        var section = button.closest("[data-freestyle-list-section]");
        var row = button.closest(ROW_SELECTOR);
        var list = section ? section.querySelector(LIST_SELECTOR) : null;
        switch (button.getAttribute("data-freestyle-action")) {
            case "add":
                if (section) {
                    addStep(section);
                }
                break;
            case "insert-snippet":
                if (form && section) {
                    insertSnippetStep(form, section);
                }
                break;
            case "remove":
                if (row && list) {
                    row.parentNode.removeChild(row);
                    renumber(list);
                }
                break;
            case "move-up":
                if (row && list) {
                    moveStep(list, row, -1);
                }
                break;
            case "move-down":
                if (row && list) {
                    moveStep(list, row, 1);
                }
                break;
        }

        event.preventDefault();
    });
})();
