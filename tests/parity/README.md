# Parity cases

`generate/*.ts` run the old TypeScript code (Insurance-Software at commit 17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37) on fixed inputs and write `cases/*.json`: the inputs and the old code's outputs. The C# tests in `tests/Insurance.Domain.Tests` read these files and require the new code to give exactly the same result.

The generators are a development tool only; the new program does not need Node or TypeScript to run.

Regenerate (needs a checkout of the old code with its `node_modules`):

```
LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/<area>.ts
```
