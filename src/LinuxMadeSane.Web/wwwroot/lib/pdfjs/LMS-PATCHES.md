# LMS PDF.js range-loading patch

The bundled PDF.js 3.11 worker's `PDFDocument.checkLastPage` eagerly walks a flat
page tree before resolving `getDocument`. For a 1,598-page, 98 MB scan this read
81 MB over 326 HTTP ranges before page 1 was available, despite `disableAutoFetch`.

The worker now defers this last-page validation when
`pdfManager.streamManager.disableAutoFetch` is true and the declared page count
is at most 10,000. Larger counts retain the eager sanity check so corrupt counts
cannot create an unbounded viewer before validation. Page-count type validation
and all normal page parsing remain; an explicitly requested page is still
validated when read. Local/full-buffer loading retains the original checks.
No content or metadata is changed in the original document.

When upgrading PDF.js, re-evaluate this behavior rather than blindly carrying
the patch forward. `tests/pdf-lazy-startup.test.cjs` exercises a large sparse PDF
with a flat page tree against the actual bundled parser and worker.
