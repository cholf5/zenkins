// The list + inspector editor ([data-split-editor]): the left column is one line per item, the right one
// is the selected item's fields - the shape Unity's Inspector has, and the one Jenkins' freestyle config
// reaches from the other side (one collapsed line per build step, opened on demand).
//
// The fields of an item are rendered once, inside the item, and this file MOVES that block
// ([data-split-detail]) into the pane. It never re-renders it: the row editors serialize what is in the
// DOM (js/freestyle-steps.js reads the nodes, not a model), and a moved <textarea> keeps the value the
// user typed while a re-created one does not. The row keeps a pointer to its block, which is also what the
// serializers read through (zenkinsSplit.fieldScope).
//
// Below the pane's breakpoint there is no pane to move into and the same markup is an accordion: app.css
// shows the selected row's block in place, and only the placement differs.
//
// Nothing here is bound to a node at load time (B-010). Selection and editing are delegated from
// `document`, and a MutationObserver re-registers an editor the Blazor circuit has replaced - the
// circuit's first render swaps the prerendered markup, which is the trap B-011 documented for the row
// templates. Summaries are installed by key (zenkinsSplit.summarize), so a list rendered after this file
// loaded still gets them.
(function () {
    "use strict";

    var EDITOR = "[data-split-editor]";
    var ROW = "[data-split-step]";
    var SELECT = "[data-split-select]";
    var DETAIL = "[data-split-detail]";
    var SLOT = "[data-split-detail-slot]";
    var PANE = "[data-split-pane]";
    var EMPTY = "[data-split-empty]";
    var TITLE = "[data-split-title]";
    var CHIPS = "[data-split-chips]";
    var WIDE = "(min-width: 60rem)";

    // key -> function (row) -> { title: string, chips: [{ text: string }] }
    var summarizers = {};

    function wide() {
        return !!(window.matchMedia && window.matchMedia(WIDE).matches);
    }

    function all(scope, selector) {
        return Array.prototype.slice.call(scope.querySelectorAll(selector));
    }

    function rowsOf(editor) {
        return all(editor, ROW);
    }

    function paneOf(editor) {
        return editor.querySelector(PANE);
    }

    function label(editor, key) {
        var raw = editor.getAttribute("data-split-labels");
        if (!raw) {
            return "";
        }

        try {
            return JSON.parse(raw)[key] || "";
        } catch (error) {
            return "";
        }
    }

    function format(editor, key, value) {
        return label(editor, key).replace("{0}", value);
    }

    function fieldValue(row, name) {
        var scope = fieldScope(row);
        var field = scope.querySelector('[data-freestyle-field="' + name + '"], [data-parameter-field="' + name + '"]');
        if (!field) {
            return "";
        }

        return field.type === "checkbox" ? (field.checked ? "true" : "false") : field.value;
    }

    // The element holding a row's fields, wherever this script has put it. The row editors serialize
    // through this, so a serializer never has to know that the block moved.
    function fieldScope(row) {
        return row._splitDetail || row;
    }

    function detailOf(row) {
        return row._splitDetail || row.querySelector(DETAIL);
    }

    function initRow(row) {
        if (row._splitDetail && row.contains(row._splitDetail)) {
            return;
        }

        var slot = row.querySelector(SLOT);
        var detail = row.querySelector(DETAIL);
        if (!slot || !detail) {
            return;
        }

        row._splitSlot = slot;
        row._splitDetail = detail;
        detail._splitRow = row;
    }

    // What a summarizer is given besides its row: the list's own words (rendered server-side into
    // data-split-labels, so no Chinese or English string lives in a .js file) and its row's values.
    function context(editor, row) {
        return {
            label: function (key) {
                return label(editor, key);
            },
            format: function (key, value) {
                return format(editor, key, value);
            },
            field: function (name) {
                return fieldValue(row, name);
            }
        };
    }

    // The one summary a row shows, computed from the values it holds right now. Called on registration and
    // after any edit inside its block, so the line and the form never disagree.
    function refresh(row) {
        var editor = row.closest(EDITOR);
        if (!editor) {
            return;
        }

        var summarize = summarizers[editor.getAttribute("data-split-editor") || ""];
        var summary = summarize ? summarize(row, context(editor, row)) : null;

        var title = row.querySelector(TITLE);
        if (title && summary) {
            setText(title, summary.title);
        }

        var chips = row.querySelector(CHIPS);
        if (chips && summary) {
            setChips(chips, summary.chips || []);
        }
    }

    // Writing the same text again is a DOM mutation, and the observer below would answer it with another
    // registration - an endless, invisible loop. Only a real difference is written.
    function setText(element, text) {
        if (element.textContent !== text) {
            element.textContent = text;
        }
    }

    function setChips(container, chips) {
        var signature = chips.join("\u0000");
        if (container._splitSignature === signature) {
            return;
        }

        container._splitSignature = signature;
        while (container.firstChild) {
            container.removeChild(container.firstChild);
        }

        for (var i = 0; i < chips.length; i++) {
            if (!chips[i]) {
                continue;
            }

            var chip = document.createElement("span");
            chip.className = "zk-chip";
            chip.textContent = chips[i];
            container.appendChild(chip);
        }
    }

    function place(editor, row) {
        var pane = paneOf(editor);
        if (!pane) {
            return;
        }

        // A block whose row is gone (the reader removed it while it was open) must not linger in the pane.
        all(pane, DETAIL).forEach(function (detail) {
            if (!detail._splitRow || !editor.contains(detail._splitRow)) {
                detail.parentNode.removeChild(detail);
            }
        });

        var empty = pane.querySelector(EMPTY);
        if (!row) {
            if (empty) {
                empty.hidden = false;
            }

            return;
        }

        var detail = detailOf(row);
        if (!detail) {
            return;
        }

        var target = wide() ? pane : row._splitSlot;
        if (target && detail.parentNode !== target) {
            target.appendChild(detail);
        }

        if (empty) {
            empty.hidden = detail.parentNode === pane;
        }
    }

    function select(editor, row) {
        if (!editor || !row) {
            return;
        }

        var previous = editor._splitSelected;
        if (previous && previous !== row) {
            previous.removeAttribute("data-selected");
            setExpanded(previous, false);

            // Its block goes home first: exactly one block may live in the pane.
            var previousDetail = detailOf(previous);
            if (previousDetail && previous._splitSlot) {
                previous._splitSlot.appendChild(previousDetail);
            }
        }

        editor._splitSelected = row;
        row.setAttribute("data-selected", "true");
        setExpanded(row, true);
        place(editor, row);
        refresh(row);
    }

    function setExpanded(row, expanded) {
        var button = row.querySelector(SELECT);
        if (button) {
            button.setAttribute("aria-expanded", expanded ? "true" : "false");
        }
    }

    function initEditor(editor) {
        if (!paneOf(editor)) {
            return;
        }

        var rows = rowsOf(editor);
        for (var i = 0; i < rows.length; i++) {
            initRow(rows[i]);
            refresh(rows[i]);
        }

        var selected = editor._splitSelected;
        if (!selected || !editor.contains(selected)) {
            selected = rows[0] || null;
        }

        if (selected) {
            select(editor, selected);
        } else {
            place(editor, null);
        }
    }

    // Idempotent: called on load, after every row mutation, and by the observer.
    function register(scope) {
        var editors = all(scope, EDITOR);
        if (scope.matches && scope.matches(EDITOR)) {
            editors.push(scope);
        }

        for (var i = 0; i < editors.length; i++) {
            initEditor(editors[i]);
        }
    }

    function rowOf(node) {
        var row = node.closest ? node.closest(ROW) : null;
        if (row) {
            return row;
        }

        // A block that has been moved into the pane is no longer inside its row; it carries the pointer.
        var detail = node.closest ? node.closest(DETAIL) : null;
        return detail && detail._splitRow ? detail._splitRow : null;
    }

    function selectFromKeyboard(row, offset) {
        var editor = row.closest(EDITOR);
        var rows = editor ? rowsOf(editor) : [];
        var index = rows.indexOf(row);
        var target = rows[index + offset];
        if (!target) {
            return;
        }

        select(editor, target);
        var button = target.querySelector(SELECT);
        if (button) {
            button.focus();
        }
    }

    function boot() {
        register(document);
    }

    window.zenkinsSplit = {
        // The summary a list's rows show, installed once per list key by that list's editor script. It is
        // applied to whatever editors carry the key - before or after this file loaded, that order does
        // not matter.
        summarize: function (key, summarize) {
            summarizers[key] = summarize;
            all(document, EDITOR).forEach(function (editor) {
                if ((editor.getAttribute("data-split-editor") || "") === key) {
                    initEditor(editor);
                }
            });
        },
        register: register,
        refresh: refresh,
        select: function (row) {
            var editor = row && row.closest ? row.closest(EDITOR) : null;
            if (editor) {
                select(editor, row);
            }
        },
        fieldScope: fieldScope
    };

    document.addEventListener("click", function (event) {
        var button = event.target.closest ? event.target.closest(SELECT) : null;
        if (!button) {
            return;
        }

        var row = button.closest(ROW);
        var editor = row ? row.closest(EDITOR) : null;
        if (editor && row) {
            select(editor, row);
        }
    });

    document.addEventListener("keydown", function (event) {
        var button = event.target.closest ? event.target.closest(SELECT) : null;
        if (!button) {
            return;
        }

        var row = button.closest(ROW);
        if (!row) {
            return;
        }

        if (event.key === "ArrowDown") {
            event.preventDefault();
            selectFromKeyboard(row, 1);
        } else if (event.key === "ArrowUp") {
            event.preventDefault();
            selectFromKeyboard(row, -1);
        }
    });

    // An edit inside the open block updates the row's line as it is typed. Capture from `document`, so a
    // block that moved into the pane is still covered.
    document.addEventListener("input", function (event) {
        var row = rowOf(event.target);
        if (row) {
            refresh(row);
        }
    }, true);

    document.addEventListener("change", function (event) {
        var row = rowOf(event.target);
        if (row) {
            refresh(row);
        }
    }, true);

    // The pane only exists at the wide breakpoint; crossing it has to move the open block, not just
    // restyle it.
    if (window.matchMedia) {
        var media = window.matchMedia(WIDE);
        var onBreakpoint = function () {
            all(document, EDITOR).forEach(function (editor) {
                if (editor._splitSelected) {
                    place(editor, editor._splitSelected);
                }
            });
        };

        if (media.addEventListener) {
            media.addEventListener("change", onBreakpoint);
        } else if (media.addListener) {
            media.addListener(onBreakpoint);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot);
    } else {
        boot();
    }

    // Blazor's enhanced navigation and its circuit's first render both replace markup this file has
    // already seen; re-registering is cheap and idempotent.
    document.addEventListener("enhancedload", boot);

    if (window.MutationObserver) {
        new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var node = added[j];
                    if (node.nodeType !== 1) {
                        continue;
                    }

                    // Only markup this file owns: a chip's text or a block moving into the pane is its own
                    // doing, and re-registering on that would answer itself.
                    if (node.matches(EDITOR) || node.querySelector(EDITOR) ||
                        node.matches(ROW) || node.querySelector(ROW)) {
                        boot();
                        return;
                    }
                }
            }
        }).observe(document.documentElement, { childList: true, subtree: true });
    }
})();
