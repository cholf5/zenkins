// F-035: the snippet affordances outside the Freestyle steps editor.
//
// - `[data-snippet-insert]` inserts a `lua-fragment` snippet's text at the textarea's caret (the inline
//   pipeline editor). The text becomes part of the script the normal save freezes; the snippet is never
//   referenced, so a later edit cannot change a saved project or a run.
// - `[data-snippet-copy]` copies a fragment to the clipboard (a repository pipeline's Pipelines tab). The
//   product never rewrites the repository file; the author pastes it.
//
// The bodies come from the page's #snippets-data JSON - one source with the Freestyle picker.
(function () {
    "use strict";

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

    function findSnippet(list, id) {
        var data = snippetData();
        var entries = list === "luaFragment" ? data.luaFragment : data.scriptStep;
        for (var i = 0; i < entries.length; i++) {
            if (entries[i].id === id) {
                return entries[i];
            }
        }

        return null;
    }

    function selectedId(button, selector) {
        var select = document.querySelector(selector);
        return select && select.selectedIndex >= 0 ? select.options[select.selectedIndex].value : "";
    }

    function insertAtCaret(textarea, text) {
        var start = typeof textarea.selectionStart === "number" ? textarea.selectionStart : textarea.value.length;
        var end = typeof textarea.selectionEnd === "number" ? textarea.selectionEnd : start;
        textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
        var caret = start + text.length;
        textarea.selectionStart = caret;
        textarea.selectionEnd = caret;
        textarea.focus();
    }

    function status(button, text) {
        var container = button.parentNode;
        var label = container ? container.querySelector("[data-snippet-status]") : null;
        if (label) {
            label.textContent = text;
        }
    }

    function onInsert(button) {
        var target = document.querySelector(button.getAttribute("data-snippet-target") || "");
        if (!target) {
            return;
        }

        var list = button.getAttribute("data-snippet-list") || "luaFragment";
        var id = selectedId(button, "[data-snippet-select=\"" + list + "\"]");
        var snippet = findSnippet(list, id);
        if (!snippet) {
            return;
        }

        insertAtCaret(target, snippet.body);
        status(button, "Inserted \"" + snippet.name + "\"; save the script to freeze it.");
    }

    function onCopy(button) {
        var list = button.getAttribute("data-snippet-list") || "luaFragment";
        var snippet = findSnippet(list, button.getAttribute("data-snippet-id") || "");
        if (!snippet) {
            return;
        }

        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(snippet.body).then(function () {
                status(button, "Copied; paste it into your pipeline file.");
            }, function () {
                status(button, "The browser refused clipboard access.");
            });
        } else {
            status(button, "The browser has no clipboard API.");
        }
    }

    // B-010: installed here, on `document`, rather than from a DOMContentLoaded scan of the page - the
    // script loads once per document, and a Blazor navigation replaces the buttons it would have found.
    document.addEventListener("click", function (event) {
        var button = event.target.closest ? event.target.closest("[data-snippet-insert],[data-snippet-copy]") : null;
        if (!button) {
            return;
        }

        event.preventDefault();
        if (button.hasAttribute("data-snippet-insert")) {
            onInsert(button);
        } else {
            onCopy(button);
        }
    });
})();
