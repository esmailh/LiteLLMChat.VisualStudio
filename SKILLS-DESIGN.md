# Skills — design

This document records how Agent Skills were added to the existing LiteLLM Visual Studio extension.
It is the architecture note for the implementation; user-facing documentation is in `SKILLS.md`.

## Constraints found in the repository

- The extension has two execution backends that are deliberately separate:
  - **LiteLLM** — `AgentHarness.RunAsync` is the extension's own round loop, with a tool registry built
    from `ContextTools`, `EditTools`, `TerminalTools`, `GitTools` and the MCP snapshot
    (`ExtensionServices.ToolsFor`). It is the only backend the Skills feature can drive with tools.
  - **Claude** — `ChatControl.SendClaudeAsync` launches the Claude Code CLI, which owns its own harness.
    It already understands Agent Skills in `.claude/skills/`, so the extension must not duplicate them.
- Settings persist through `UserSettings` in `settings.json`; conversations persist per solution through
  `ChatSessionStore` / `ChatSessionState`.
- The chat composer already has one popup used for `@` files, `#` MCP tools and `/` MCP prompts or
  Claude slash commands. Skills extend that popup rather than adding a second one.
- The Dashboard is a tool window with per-feature tabs; MCP is the pattern to follow.
- Tool execution needs `DTE` only for some tools. Pure filesystem logic belongs in `LiteLLM.Core` so the
  test project (whose only reference is Core) can cover it.
- There is no YAML package. Adding one would be a new dependency in a net48 VSIX; the Skills
  frontmatter subset is small and is parsed by hand.

## Storage and discovery

Defaults (all configurable from the Dashboard's Skills tab):

| Scope | Location | Precedence |
| --- | --- | --- |
| Project | `<solution>/.agents/skills` | 1 (highest) |
| Project (compatible) | `<solution>/.github/skills` | 2 |
| Project (compatible) | `<solution>/.claude/skills` | 3 |
| Personal | `%USERPROFILE%\.llm123\skills` | 4 (lowest) |

Only immediate child directories are enumerated; a child is a skill only when it contains `SKILL.md`.
The scanner never walks the whole filesystem.

Duplicate names are deterministic: the highest-precedence valid, enabled definition is effective.
Every other definition with that name is marked *shadowed* and shown with its path in the Dashboard.
The user resolves a conflict by disabling the definition they do not want (enabled state is keyed by
full path, not by name) or by editing/removing one of them.

Refresh avoids repeated reads: the registry records a signature made from each discovered
`SKILL.md` path, length and last-write time plus the disabled-path set. A refresh with an unchanged
signature returns the cached snapshot without reading files.

## Validation

`SKILL.md` must start with YAML frontmatter delimited by `---` lines. Supported fields, matching the
Agent Skills specification:

- `name` (required) — 1–64 characters, lowercase `a-z`, `0-9`, `-`; no leading/trailing hyphen, no
  consecutive hyphens; must equal the skill directory name.
- `description` (required) — 1–1024 characters.
- `license`, `compatibility` (≤ 500), `metadata` (string → string map), `allowed-tools` (optional).

Unknown keys are ignored for forward compatibility. A malformed file produces a visible row with the
reason; it is never offered to a model. Path traversal in a skill's own directory is irrelevant to
discovery, but every resource read is confined to that directory by `PathGuard`.

## Invocation

### LiteLLM (automatic)

Before the first round `AgentHarness` prepares a registry snapshot for the solution and resolves the
conversation's `SelectedSkills`. Automatic mode sends only the *catalog* (name + description) in the
system prompt and offers two tools:

- `load_skill(name)` — returns the `SKILL.md` body and its resource listing.
- `read_skill_file(name, path)` — reads a bounded text file inside that skill's directory.

The model is told that a skill is loaded only when the tool result is present. This is progressive
disclosure: metadata first, full instructions on demand. When tool calling is off, automatic mode is
unavailable and says so; manual selection still works because its body is injected directly.

### Manual (both backends)

Selected skills live on the conversation (`ChatSessionState.SelectedSkills`). They are chosen from the
`/` list — where skills are the first rows, a ticked row deselects — and appear as chips above the
composer with an `×`. Their full bodies are injected into the system prompt for every turn of that
conversation. Selection is not a permission grant.

### Slash command

`/skill <name>` and `/skill <name> <instructions>` are parsed before the message is sent, on both
backends. The command is handled locally and never sent as ordinary text. The remaining instructions
become the user message. An unknown name draws a row with suggestions and leaves the composer intact.
A prefix that matches exactly one skill is accepted; a prefix matching several is reported as
ambiguous. Ordinary messages that merely start with `/` keep their existing meaning (MCP prompts on
LiteLLM, Claude commands on the Claude backend).

### Claude backend

- Automatic: delegated to the CLI's native `.claude/skills` support. The extension does not inject a
  catalog on this backend.
- Manual / `/skill`: a skill under `.claude/skills` is referenced by a short directive so the CLI loads
  it natively (no duplicate body). A skill from any other location is injected in full, because the
  CLI cannot discover it.

## Security

- Skills are instructions, not code. Scripts in `scripts/` are never executed; they can only be read
  like any other resource. No tool is added to execute them.
- Skill content is labeled untrusted in the system prompt, after the extension's own rules and before
  repository instructions. It cannot grant permissions: every edit and command still goes through the
  existing approval paths.
- Resource reads are confined to the skill directory and bounded in size. Binary files are refused.
- Delete is confirmed and only accepts a direct skill directory under a configured root.
- Import refuses to overwrite, refuses reparse points, and caps file count and total size.
- Settings and conversations remain backward compatible: new fields default to the old behavior.
  Existing `settings.json` and session files load unchanged.

## Code layout

New Core files under `src/LiteLLM.Core/Skills/`:

- `SkillInfo.cs` — model, scope, resources.
- `SkillFrontmatter.cs` — parser and validation.
- `SkillRegistry.cs` — discovery, precedence, conflicts, cache.
- `SkillPrompt.cs` — catalog and loaded-instruction blocks.
- `SkillTools.cs` — tool definitions and execution.
- `SkillSlashCommand.cs` — command grammar and suggestions.
- `SkillStorage.cs` — create/save/delete/import with path guards.
- `SkillGeneration.cs` — AI draft prompt and response extraction.

Visual Studio project additions:

- `SkillsDashboardControl.xaml(.cs)` — Dashboard tab.
- `SkillEditorDialog.xaml(.cs)` — create/edit/review draft.

Touched existing files:

- `Models.cs`, `ChatState.cs`, `ChatSessionStore.cs` — settings and per-chat selection.
- `AgentPrompt.cs` — one skills block in the system prompt.
- `ExtensionServices.cs` — registry ownership, tool availability, skill preparation.
- `AgentHarness.cs` — catalog, tools, dispatch, rows.
- `ChatControl.xaml(.cs)` — chips, `/skill`, the `/` list's skill rows, Claude injection.
- `DashboardControl.xaml` — Skills tab.
- `README.md`, `SKILLS.md` — documentation.
