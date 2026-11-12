// F-002: plain-HTML POST forms (the organization switcher and the Owner-only management forms) need
// the antiforgery token that App.razor rendered into <meta name="request-verification-token">.
// Forms carry data-zenkins-antiforgery; the token is copied into a hidden field on submit (and once
// on load, so a scripted/automated client can also read it from the DOM). Interactive Blazor circuits
// have no HttpContext, so the token cannot be rendered by the component itself.
(function () {
    "use strict";

    function requestToken() {
        var meta = document.querySelector('meta[name="request-verification-token"]');
        return meta ? meta.getAttribute('content') : null;
    }

    function fill(form) {
        var token = requestToken();
        if (!token) {
            return;
        }

        var input = form.querySelector('input[name="__RequestVerificationToken"]');
        if (!input) {
            input = document.createElement('input');
            input.type = 'hidden';
            input.name = '__RequestVerificationToken';
            form.appendChild(input);
        }

        input.value = token;
    }

    function fillAll() {
        var forms = document.querySelectorAll('form[data-zenkins-antiforgery]');
        for (var i = 0; i < forms.length; i++) {
            fill(forms[i]);
        }
    }

    // Capture phase: runs before any other submit listener and before the request leaves the page.
    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (form && form.matches && form.matches('form[data-zenkins-antiforgery]')) {
            fill(form);
        }
    }, true);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', fillAll);
    } else {
        fillAll();
    }
})();
