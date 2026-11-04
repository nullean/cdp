# Checks

Each check is a library method and a journey command. In a journey it lists what it found, and with `--strict` fails
the step instead.

## Accessibility

```csharp
var violations = await page.AuditAsync();            // or AuditAsync("main") for one element
Console.Write(AccessibilityAudit.Format(violations));
```

```text
violations: 12
[serious] aria-hidden-focus: Content hidden with aria-hidden must not be focusable (1 node)
  - div[aria-hidden="true"]
[critical] button-name: Buttons need an accessible name (1 node)
  - button
[serious] color-contrast: Text contrast meets WCAG AA (4.5:1, or 3:1 for large text) (1 node)
  - p (1.92:1)
…
```

Our own rules, with no third-party engine. Rule names and the layout follow agent-browser's `a11y` (which runs
axe-core), so results read alike.

| Rule | Impact | From |
|---|---|---|
| `image-alt` | critical | images without alternative text, or `alt=""` when decorative |
| `button-name` | critical | buttons without an accessible name |
| `label` | critical | text boxes, comboboxes, checkboxes, radios, sliders, … without a label |
| `link-name` | serious | links without an accessible name |
| `color-contrast` | serious | text under WCAG AA: 4.5:1, or 3:1 for text of 24px or bold 18.66px |
| `document-title` | serious | an empty or missing `<title>` |
| `html-has-lang` | serious | `<html>` without `lang` |
| `aria-hidden-focus` | serious | focusable content inside `aria-hidden="true"` |
| `heading-order` | moderate | a heading level that skips one going down |
| `landmark-one-main` | moderate | not exactly one `main` landmark |
| `empty-heading` | minor | headings without text |
| `duplicate-id` | minor | the same id twice |

Names come from Chrome's accessibility tree, which computes them as assistive technology does. Contrast composes the
text colour over the nearest solid background through semi-transparent layers; text over a background image or
gradient is skipped as undecidable, and disabled controls are exempt, as WCAG says. It finds the common problems; it is
not a full WCAG audit.

```text
a11y                 # list
a11y -s main         # one element's subtree
a11y --strict        # fail the step on any violation
```

## Broken links

```csharp
var links = await page.LinksAsync();                  // or LinksAsync("main")
var results = await page.CheckLinksAsync(links);
foreach (var link in results.Where(l => l.Broken))
    Console.WriteLine($"{link.Outcome} {link.Url}"); // 404 http://localhost:5000/old-page
```

`LinksAsync` collects every `a[href]` and `area[href]` with an http, https or file URL, once each, without fragments,
in document order. `CheckLinksAsync` fetches them through the browser, four at a time: a GET outside the page, so the
page's CORS rules don't apply and page script never sees it, with the body closed unread. Cookies go only to the
page's own origin. File links are checked on disk.

In a journey, `links` checks same-site links only. `--external` adds links to other sites, but only hosts in the
allow list are fetched, so a page cannot make a run send requests anywhere.

```text
links: 2 checked, 1 broken, 2 on other sites not checked (--external)
404 /missing-page
```

`--all` lists working links too; `--strict` fails the step on a broken one. Same-site links print without their
origin, so a transcript does not depend on the port. A link behind HTTP authentication reports 401: `set credentials`
applies to the page's own requests only.

## Failed requests

```text
network requests --failed
```

```text
GET http://localhost:5000/missing.png (Image) 404
```

Requests answered with 400 or more, and requests that got no answer at all (`net::ERR_NAME_NOT_RESOLVED`, a refused
connection). Requests the allow list refused are left out, since the run reports those already, and so are requests a
navigation cancelled. `--strict` fails the step when anything is listed. In code, filter
[`page.RecordNetworkAsync()`](network.md)'s requests on `Status` and `Error`.

## Console errors

`errors` lists uncaught errors, one per line with their location. To fail a journey on them, snapshot the `errors`
checkpoint: an empty list that becomes non-empty is a difference.
