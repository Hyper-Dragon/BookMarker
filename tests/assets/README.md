# Test assets

`sample-3page.pdf` is a deterministic three-page fixture generated for this project, so there is
no third-party content and nothing to attribute:

1. title and paragraphs,
2. a heading, paragraphs and a small bordered table (something for the layout model to find),
3. a heading, a bullet list and a closing line.

It exists for `scripts/acceptance.sh`, which converts it through the service and then through a
direct `marker` CLI run inside the container, and compares the two markdown outputs. That
comparison is the real regression test for an engine pin bump.

The generator is not shipped: it is a one-off fixture tool, not part of the build or the service.
If the fixture ever needs changing, regenerate it, keep the page count and the table, and
re-run the acceptance script.
