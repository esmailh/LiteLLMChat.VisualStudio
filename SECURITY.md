# Security policy

## Supported versions

Security fixes go into the latest 1.7.x release. Older versions are not maintained.

## Reporting a vulnerability

Please report vulnerabilities privately, not in a public issue:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability** and fill in the advisory form.

Include the extension version, the Visual Studio version, the backend (LiteLLM or Claude Code),
and the smallest reproduction you can. **Do not include API keys, MCP tokens, gateway URLs with
credentials, or confidential source code.** Redact logs and screenshots; the extension masks
credential-looking values, but paths and prompts may still be present.

This is a volunteer project: there is no bug bounty and no guaranteed response time, but reports
are read and acknowledged.

## Scope

The extension runs inside Visual Studio and talks only to systems you configure: an
OpenAI-compatible or LiteLLM endpoint, MCP servers you add and explicitly enable, and the Claude
Code CLI when that backend is selected. Settings, conversations, the token usage ledger and the
DPAPI-protected secret stores live under `%LOCALAPPDATA%\LiteLLM.VisualStudio`.

Reports that are in scope include, for example:

- reading or writing outside the open solution root through the workspace tools;
- an MCP tool call that runs without the approval the policy requires;
- secrets (API keys, MCP bearer tokens, OAuth grants) appearing in logs, exports, the usage
  ledger, or a saved conversation;
- injection through MCP tool names, arguments, resources or prompts;
- command or argument injection in the Git wrapper;
- TLS, redirect, or OAuth handling in an MCP transport that sends a credential somewhere it
  should not;
- a crafted VSIX or settings file that executes code without the user enabling it.

Vulnerabilities in Visual Studio itself, in a gateway or model provider, in an MCP server, or in
the Claude Code CLI are out of scope for this repository; report those to the respective project.
