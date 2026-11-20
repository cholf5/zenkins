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

    function fieldScope(row) {
        // The fields may have been moved into the inspector pane by js/split-list.js, which leaves the row a
        // pointer to them. When it has not run, they are still the row's own children.
        return row._splitDetail || row;
    }

    function fieldValue(row, name) {
        var field = fieldScope(row).querySelector('[data-freestyle-field="' + name + '"]');
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

    // F-030's step line: what a collapsed row shows instead of its whole form - the name (or the script's
    // first line, so an unnamed step still says what it does) plus the settings that are not the default.
    // The words come from the list's data-split-labels, so this file holds none of either language.
    function stepSummary(row, ctx) {
        var name = (ctx.field("name") || "").trim();
        var title = name || firstScriptLine(ctx.field("script")) || ctx.label("unnamed");
        var chips = [];

        var stage = (ctx.field("stage") || "").trim();
        if (stage) {
            chips.push(stage);
        }

        var env = countLines(ctx.field("env-lines"));
        if (env > 0) {
            chips.push(ctx.format("env", env));
        }

        var artifacts = countList(ctx.field("artifacts-text"));
        if (artifacts > 0) {
            chips.push(ctx.format("artifacts", artifacts));
        }

        var timeout = (ctx.field("timeout-seconds") || "").trim();
        if (timeout) {
            chips.push(ctx.format("timeout", timeout));
        }

        var attempts = (ctx.field("retry") || "").trim();
        if (attempts && attempts !== "1") {
            chips.push(ctx.format("attempts", attempts));
        }

        var runsOn = (ctx.field("runs-on") || "").trim();
        if (runsOn) {
            chips.push(ctx.format("runsOn", runsOn));
        }

        if (ctx.field("continue-on-error") === "true") {
            chips.push(ctx.label("continueOnError"));
        }

        return { title: title, chips: chips };
    }

    function firstScriptLine(script) {
        var lines = (script || "").split("\n");
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (line.length > 0) {
                return line;
            }
        }

        return "";
    }

    function countLines(text) {
        var lines = (text || "").split("\n");
        var count = 0;
        for (var i = 0; i < lines.length; i++) {
            if (lines[i].trim().length > 0) {
                count++;
            }
        }

        return count;
    }

    function countList(text) {
        var items = (text || "").split(",");
        var count = 0;
        for (var i = 0; i < items.length; i++) {
            if (items[i].trim().length > 0) {
                count++;
            }
        }

        return count;
    }

    function splitList() {
        return window.zenkinsSplit || null;
    }

    // The four stems of this editor share one summary; the editor's own key names the words to use.
    var listKeys = ["steps", "always", "success", "failure"];

    function installSummaries() {
        var api = splitList();
        if (!api) {
            return;
        }

        for (var i = 0; i < listKeys.length; i++) {
            api.summarize(listKeys[i], stepSummary);
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

    // B-011: a <template>'s children live in template.content only when the HTML parser put them there.
    // Blazor's renderer inserts a component's children with appendChild, which lands them on the template
    // element itself - so once the circuit has activated the prerendered page, content is empty and the row
    // is a plain child node. Reading content alone threw "Cannot read properties of null (reading
    // 'cloneNode')" and "Add step" did nothing at all.
    function templateRow(template) {
        return template.content.firstElementChild || template.firstElementChild;
    }

    function addStep(section) {
        var template = section.querySelector("template");
        var list = section.querySelector(LIST_SELECTOR);
        if (!template || !list) {
            return;
        }

        var source = templateRow(template);
        if (!source) {
            return;
        }

        list.appendChild(source.cloneNode(true));
        renumber(list);
        afterMutation(section, list);
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

        var source = templateRow(template);
        if (!source) {
            return;
        }

        var row = source.cloneNode(true);
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
        afterMutation(section, list);
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

    // A row list changed: the inspector has to see the new row (its block is still in the row, not in the
    // pane) and open it, which is what makes "add" land the reader in the fields they just created.
    function afterMutation(section, list) {
        var api = splitList();
        if (!api) {
            return;
        }

        api.register(section);

        var all = rows(list);
        if (all.length > 0) {
            api.select(all[all.length - 1]);
        }
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
                    var api = splitList();
                    if (api && section) {
                        api.register(section);
                    }
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

    installSummaries();

    // F-046: a row a drag moved. The frame reorders the nodes; numbering them is this list's own job (four
    // lists share this script, and each numbers its rows in its own attribute).
    document.addEventListener("split-reorder", function (event) {
        var list = event.target && event.target.closest ? event.target.closest(LIST_SELECTOR) : null;
        if (list) {
            renumber(list);
        }
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", installSummaries);
    }

    // Load order between this file and js/split-list.js must not matter, and a re-rendered page must get
    // its lines back: summarizing is idempotent, so both hooks are safe to repeat.
    document.addEventListener("enhancedload", installSummaries);
})();
