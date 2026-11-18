// F-020: the Freestyle steps editor. The form is a plain HTML form posting to the real endpoint
// (antiforgery like every other form); this script is the dynamic row layer the server cannot render
// statelessly: add / remove / reorder a step, then serialize the rows into the one hidden StepsJson
// field the endpoint parses. The server (FreestyleSteps.BuildSpecJson) rebuilds and re-validates the
// whole spec from that payload - the browser never authors a spec, it only collects field values.
(function () {
    "use strict";

    var FORM_SELECTOR = "form[data-freestyle-steps-form]";
    var ROW_SELECTOR = "[data-freestyle-step]";

    function rows(form) {
        return Array.prototype.slice.call(form.querySelectorAll(ROW_SELECTOR));
    }

    function fieldValue(row, name) {
        var field = row.querySelector('[data-freestyle-field="' + name + '"]');
        if (!field) {
            return "";
        }
        return field.type === "checkbox" ? (field.checked ? "true" : "false") : field.value;
    }

    function serialize(form) {
        var drafts = rows(form).map(function (row) {
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

        var target = form.querySelector("#freestyle-steps-json");
        if (target) {
            target.value = JSON.stringify(drafts);
        }
    }

    function renumber(form) {
        var all = rows(form);
        for (var i = 0; i < all.length; i++) {
            var label = all[i].querySelector("[data-freestyle-step-index]");
            if (label) {
                label.textContent = String(i + 1);
            }
        }
    }

    function addStep(form) {
        var template = form.querySelector("#freestyle-step-template");
        var list = form.querySelector("[data-freestyle-steps-list]");
        if (!template || !list) {
            return;
        }

        list.appendChild(template.content.firstElementChild.cloneNode(true));
        renumber(form);
    }

    function removeStep(row) {
        row.remove();
    }

    function moveStep(form, row, offset) {
        var all = rows(form);
        var index = all.indexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= all.length) {
            return;
        }

        // insertBefore the neighbour: offset -1 swaps with the previous sibling, +1 inserts before
        // the next-next sibling (or appends at the end).
        var reference = offset < 0 ? all[index - 1] : all[index + 1].nextSibling;
        row.parentNode.insertBefore(row, reference);
        renumber(form);
    }

    function init() {
        var forms = document.querySelectorAll(FORM_SELECTOR);
        for (var f = 0; f < forms.length; f++) {
            (function (form) {
                form.addEventListener("submit", function () {
                    serialize(form);
                });

                form.addEventListener("click", function (event) {
                    var button = event.target.closest ? event.target.closest("[data-freestyle-action]") : null;
                    if (!button) {
                        return;
                    }

                    var row = button.closest(ROW_SELECTOR);
                    switch (button.getAttribute("data-freestyle-action")) {
                        case "add":
                            addStep(form);
                            break;
                        case "remove":
                            if (row) {
                                removeStep(row);
                                renumber(form);
                            }
                            break;
                        case "move-up":
                            if (row) {
                                moveStep(form, row, -1);
                            }
                            break;
                        case "move-down":
                            if (row) {
                                moveStep(form, row, 1);
                            }
                            break;
                    }

                    event.preventDefault();
                });
            })(forms[f]);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
