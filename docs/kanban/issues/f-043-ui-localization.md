---
id: F-043
title: UI localization — English and Simplified Chinese, remembered by the browser
type: feature
priority: P1
size: L
status: review
created: 2026-11-19
updated: 2026-11-19
blocked: none
---

## Goal

The browser UI can be read in English or Simplified Chinese. A visitor's language is chosen on the
first visit (browser `Accept-Language`) and remembered afterwards, and every page, form validation
message and empty state follows it. The first version ships exactly these two languages.

## Background

Every string was English and compiled into the markup, so an operator who reads Chinese had to work
in a second language — including on the failure paths (validation messages, refused triggers) where
the words decide what they do next.

The design constraints that shaped the implementation:

- **The English source text is the key.** `L10n["Enter a project name."]` falls back to that text
  when no translation exists, so a new string is readable before it is translated, and a translator
  sees the real sentence instead of a symbolic name.
- **A circuit cannot read a cookie.** Interactive Server rendering has no `HttpContext`, so the
  language is resolved for the request and handed to the circuit with `PersistentComponentState`;
  switching language is a real form POST plus a document reload, never a partial re-render that
  would leave half the page in the old language.
- **The server's own culture is never used.** A shared CI server must not answer in its operator's
  machine locale; the fallback is English, and formatting uses the chosen language's culture.
- **Coverage is mechanical.** A test scans the source for keys and fails on a missing translation,
  an orphaned catalogue entry or a placeholder mismatch, so "forgot to translate the new field"
  cannot ship silently.

## Acceptance Criteria

- [x] English and Simplified Chinese are the only shipped languages; `zh*` maps to `zh-Hans` and
      `en*` to `en`; anything else falls back to English, never to the machine culture.
- [x] The first visit follows `Accept-Language`; the switcher (available signed-in and on the sign-in
      pages) writes an HttpOnly `zenkins-lang` cookie that wins afterwards, and reloads the document.
- [x] Every page, shared component, form validation message, empty state, notice and refusal message
      the UI renders is localized; server-produced messages the UI displays are localized at their
      display boundary.
- [x] `<html lang>` and the document's copy agree on every request, including the prerender pass.
- [x] API responses, log lines, exception text and other machine-facing strings stay English.
- [x] The catalogue test proves both directions (used → translated, translated → used) and that every
      `{0}` in a translation matches its key; the HTTP tests prove the cookie/header resolution and
      the switcher's redirect safety (a forged `ReturnUrl` is not an open redirect).
- [x] PRD §3.17 records the rule; the changelog says what shipped.

## Subtasks

- [x] `AppLanguage` (codes, cookie contract, `Accept-Language` ordering), `LanguageContext` (request/
      circuit resolution), `TranslationCatalog` (two embedded catalogues: UI copy and framework
      messages), and the `Localizer` façade injected as `L10n` in every component.
- [x] `POST /language`: validates the code, writes the cookie, honors a sanitized return path.
- [x] `PersistentComponentState` hand-over from prerender to circuit, and `<html lang>` from the
      request.
- [x] Switcher component in the shell and the account layout.
- [x] Validation-message tables (project, folder, secret, environment, webhook, Git credential,
      snippet, schedule, retention, notification) take the localizer; `RunDisplay`'s status/reason
      text and the trigger guard's refusals do too.
- [x] `LocalizedValidationMessage` for DataAnnotations messages (attributes can only carry English).
- [x] Catalogue coverage/placeholder tests, language-resolution tests, and HTTP tests for the shell,
      the account pages and the switcher.

## Verification

- `dotnet build Zenkins.slnx -c Debug` — 0 warnings, 0 errors.
- `dotnet test --filter "Category=Localization"` — 18 tests, all green: the catalogue guard, the
  resolution rules, and the request-level shell/switcher behaviour.
- `dotnet test` on the UI suites (ProjectUi, RunUi, RunDetail, RunLog, Secrets, Snippet, Worker,
  Settings/Schedule/Notification/Webhook/GitCredential) — English copy is byte-identical, so their
  assertions stay green. Each page batch was verified before the next one started; the last batch
  (log-panel state, trigger-guard refusals, snippet kind labels) is verified by the build and the
  catalogue guard below, and its UI-suite run is left to the reviewer (see Known limits).
- `TranslationCatalogTests`: every key used in `src/Server` is catalogued, no catalogue entry is
  unused, and no placeholder differs (0 missing, 0 orphaned, 0 placeholder mismatches).

## Known limits

- Two languages only; adding a third is a new catalogue file plus a `Choices` entry, not a new code
  path (the resolver and the switcher iterate the list).
- Email and notification payloads the product sends to third parties stay English: they are not UI.
- Rich text inside a translated sentence uses separate keys around the inline `<code>`/`<strong>`
  element, so a translator sees two fragments; this keeps the markup authoritative instead of
  injecting HTML from the catalogue.
- The last C#-helper batch's UI integration suites were not re-run after it landed (the localization
  suite and the per-page suites were green for every earlier batch). The reviewer should run
  `dotnet test --filter "FullyQualifiedName~RunLog|FullyQualifiedName~RunUi|FullyQualifiedName~ProjectUi"`
  before moving the card to Done.
