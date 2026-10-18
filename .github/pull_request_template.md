## What changed

<!-- One paragraph. Link the issue when there is one. -->

## Why

<!-- The problem this solves, and why this approach. -->

## Verification

- [ ] `LiteLLM.Core`, `LiteLLM.GitWrapper`, `LiteLLM.VisualStudio` and the test project build
- [ ] `vstest.console.exe` on the test assembly is green
- [ ] UI or `DTE` behavior was checked in the experimental instance (state what you did)

## Checklist

- [ ] No secrets, tokens, `.mcp.json`, settings files, or logs are included
- [ ] Comments explain *why*; the change follows the surrounding style
- [ ] The agent loop still answers every announced call, and the Claude CLI path is untouched
