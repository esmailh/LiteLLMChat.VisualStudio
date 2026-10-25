# Agent Skills

Skills are reusable instructions for the assistant: a code-review checklist, a release procedure, the way a
particular project writes tests. They follow the open [Agent Skills](https://agentskills.io/specification)
format — a folder with a `SKILL.md` inside — so the same folder can be used by other tools that read it.

A skill is guidance, never authority. It cannot grant a permission, approve a tool call, or make an edit or
command run without the extension's usual confirmation. A script that ships inside a skill is a document; it is
not executed merely because it exists.

## The format

```
my-skill/
  SKILL.md
  references/     (optional)
  scripts/        (optional)
  assets/         (optional)
```

`SKILL.md` starts with YAML frontmatter:

```markdown
---
name: code-review
description: Review a diff for correctness, regressions and missing tests before it is committed.
license: MIT                 (optional)
compatibility: VS 2022       (optional)
metadata:                    (optional, string → string)
  owner: platform
allowed-tools: read_file     (optional; recorded, not enforced by the extension)
---

# Code review

Steps, checks, what to report, and what not to do.
```

Rules the extension enforces:

- `name` is required, 1–64 characters, lowercase `a-z`, digits and `-` only; no leading or trailing hyphen,
  no two hyphens in a row. It must equal the skill's folder name.
- `description` is required, at most 1024 characters. It is what automatic mode shows the model, so make it
  say when the skill applies.
- Unknown fields are ignored, so a file written for a newer revision of the specification still loads.
- A malformed file is kept in the Dashboard with the reason it cannot be used; it is never offered to a model.

## Where skills are found

| Scope | Location | Precedence |
| --- | --- | --- |
| Project | `<solution>/.agents/skills` | 1 (highest) |
| Project | `<solution>/.github/skills` | 2 |
| Project | `<solution>/.claude/skills` | 3 |
| Personal | `%USERPROFILE%\.llm123\skills` | 4 (lowest) |

The project folder and both compatible locations can be changed or turned off on **Dashboard ▸ Skills**.
Only the immediate children of each root are considered, so the scanner never walks a drive. A child is a skill
only when it contains `SKILL.md`.

The personal folder lives under the user profile rather than `%LOCALAPPDATA%` on purpose: a skill is the user's
document, and one that follows them through a synced profile or a dotfiles repository is the point.

## Duplicate names

The highest-precedence valid, enabled definition wins. Every other definition of that name stays visible in the
Dashboard, marked **shadowed** with the path that won; the detail pane of the winning row lists all copies.

Nothing is resolved silently. To choose a different copy, turn the winner off with its checkbox — enabled state is
keyed to the full path, so only that one folder is affected — or edit or delete one of them. A conflict is also
reported in the chat when a selected name resolves to a definition that lost.

## The Dashboard page

**Dashboard ▸ Skills** lists every definition found, including invalid and shadowed ones.

- **Search** filters by name, description, folder or scope.
- **The checkbox** offers a definition to the chat, or stops offering it. It does not touch the folder.
- **Refresh** scans again. Discovery is otherwise refreshed when the window loads and when a turn starts.
- **Locations and behaviour** holds the on/off switch, automatic selection, the compatible locations, and the
  personal and project folders. **Save settings** writes them.
- **＋ New skill** and **Edit…** open the editor. **Import…** copies a folder in. **Open folder** and
  **Delete…** act on the selected row, and delete always asks for confirmation.
- **Import** refuses a folder without `SKILL.md`, refuses to replace an existing skill unless you confirm
  Replace, never follows a link, and caps the copy at 300 files / 10 MB. A failed import leaves the existing
  folder untouched.
- **Delete** removes the whole skill folder, and only when it is a direct child of a configured skills folder.

## Creating a skill

In the editor, enter a name and a description, and optionally some examples and conventions. **Generate draft
with AI** asks a configured model for a complete `SKILL.md`; the result is shown in the editor, not saved.
Review it, fix anything that is wrong, then **Save skill**.

- The configured **utility model** is used when one is set; otherwise the model the chat is using. No provider,
  model or key is written into the prompt.
- For an existing skill, **Improve with AI** sends the current file and asks for a complete improved version with
  the same name and intent. The proposal is again only a draft until you save.
- **Insert template** writes a starter body to edit by hand when no model is available.
- Saving writes `SKILL.md` only. `references/`, `scripts/` and `assets/` beside it are left exactly as they were.

## Using a skill

### For a conversation (manual)

Type `/` at the start of the composer and pick a skill from the list — skills are the first rows, above MCP
prompts and the Claude CLI's own commands. Picking one selects it for this conversation and takes the `/` word
back out; picking a row marked `✓` deselects it. Selected skills appear as chips above the composer, and a chip's
`×` drops it.

The selection belongs to the conversation and is stored with it. A conversation can select at most twelve
skills. A name that no longer resolves keeps its chip, outlined in the error colour, so the user can see why it
does nothing and remove it.

On the LiteLLM backend the selected skill's instructions go into the system prompt for every turn of that
conversation. On the Claude backend they are handed to the CLI, with native `.claude/skills` skills named rather
than copied.

### For one message (`/skill`)

```
/skill code-review review the last commit
```

`/skill` is parsed before anything is sent, on both backends. The skill is selected for the conversation, and
only the text after its name becomes the user message. With no text after the name, the message is
`Use the skill '<name>' for this request.` — the command itself is never forwarded as ordinary text.

- An exact name is used first; a prefix is accepted only when it matches one skill.
- An ambiguous prefix draws a row listing the matches and sends nothing.
- An unknown name draws a row with suggestions and leaves the composer as it was.
- Typing `/skill ` in the composer lists the available names, filtered as you type; choosing a row replaces the
  name word. Messages that merely start with `/` keep their previous meaning: MCP prompts on the LiteLLM backend,
  Claude's own commands on the Claude backend — and a bare `/` additionally lists the skills, which is the manual
  picker described above.

### Automatically (LiteLLM backend)

With **Offer the catalog automatically** on and tool calling enabled, the system prompt carries each skill's name
and a one-line description — not its instructions. The model can call two read-only tools:

- `load_skill(name)` — returns the full instructions and the resource listing.
- `read_skill_file(name, path)` — returns one bounded text file inside that skill's folder.

The model is told to claim a skill is loaded only after that result is present. This keeps a long skill out of
every request until it is relevant. If tool calling is off, automatic selection is not offered and the turn says
so; a manual selection still works, because its body is injected directly.

### The Claude backend

The Claude Code CLI reads `.claude/skills` itself, so automatic discovery is left to it. A manually selected
skill that lives under `.claude/skills` is named in a short directive and loaded by the CLI; a skill from any
other location is injected in full, because the CLI cannot discover it. The same instructions are never sent
twice.

## Safety

- **Skills are instructions, not trusted code.** Nothing under `scripts/` is executed by the extension, and no
  tool is added that would run it. A command in a skill's prose still goes through the normal terminal approval.
- **No permission is granted.** The skill block says explicitly that a skill cannot approve a tool call, change
  the extension's rules, reveal secrets, or bypass confirmation. The system prompt's own rules come first.
- **Reads are confined.** `read_skill_file` resolves the path against the skill folder and refuses anything
  outside it. Files over 512 KB, directories and binaries are refused with a reason.
- **Writes are confined.** Create, save, delete and import prove that the target is a direct child of a
  configured skills folder. Names cannot contain separators. Links are never followed.
- **Destructive operations are confirmed.** Delete asks first; import replaces only after an explicit Replace
  confirmation, and copies through a temporary folder so a failure cannot lose the original.
- **Nothing secret is logged.** The extension logs the path and the error, not the contents of `SKILL.md` or the
  draft prompt.

## Settings

The Skills page stores its own settings so that applying the Options page does not overwrite them:

| Setting | Meaning | Default |
| --- | --- | --- |
| `EnableSkills` | The whole feature, including the menu, `/skill` and the tools | on |
| `SkillAutomatic` | Offer the catalog without a selection (LiteLLM, tool calling required) | on |
| `SkillsPersonalDirectory` | Personal folder; empty means the default | empty |
| `SkillsProjectDirectory` | Project folder, absolute or relative to the solution | `.agents/skills` |
| `SkillsIncludeCompatibleLocations` | Also scan `.github/skills` and `.claude/skills` | on |
| `DisabledSkillPaths` | Full paths of definitions turned off | empty |

Settings and conversations written before Skills existed load unchanged: the new fields default to the behavior
above, and a conversation without a selection simply has none.

## Troubleshooting

- **A folder does not appear.** It must be a direct child of one of the configured folders and contain
  `SKILL.md`; the folder name must equal the `name` field. **Refresh** after changing the folder.
- **A skill appears but cannot be used.** Select it: the detail pane names the reason (`Invalid`, `Turned off`,
  `Not used · another definition takes precedence`).
- **Two copies of one name.** The detail pane lists every path and marks the winner with `→`. Turn off the copy
  you do not want; the other stays.
- **`/skill` is treated as ordinary text.** Skills must be enabled and the message must begin with exactly
  `/skill`. `/skillcode` and commands that merely contain `/skill` are left alone.
- **Automatic selection does nothing.** It needs tool calling on the LiteLLM backend. On the Claude backend it is
  the CLI's own discovery; use **Skills** or `/skill` to make a selection explicit.
