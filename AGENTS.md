# NexusRouter — Agent Instructions

This repository is spec-driven. **`.kiro/` is the source of truth** for Kiro, Cursor and
any other agent. Do not copy steering or spec content into `.cursor/rules/` or skills.

## Specs

- `.kiro/specs/nexusrouter/` — the router (T-01 → T-53)
- `.kiro/specs/dropcopy-simulator/` — the DropCopy simulator

Task IDs repeat across specs. The prompt must name the spec and the task ID.
If either is missing, ask. Never assume the spec.

## What to read before implementing

Read only what the current task needs. Do not load a full spec or every steering file.

1. Get the task block with `./scripts/task.sh <spec> <ID>`. Never open `tasks.md` in full.
2. Read only the files and sections listed in the task's **Entradas** (design.md,
   requirements.md, steering). Locate sections by grepping the heading.
3. Read `.kiro/steering/coding-standards.md` when the task writes or changes C#.
4. Read `.kiro/steering/b3-guidelines.md` when the task touches FIX, session, parsing,
   routing, or B3 fields (only the cited section if the task names one).
5. Read `.kiro/steering/git-workflow.md` only when the user asks for commit, push, or a PR.
6. For tasks that depend on earlier ones, inspect the existing code (grep, signatures)
   instead of re-reading the earlier tasks' specs.

## How to execute work

- Implement **one** task at a time: the one named in the prompt. Respect phase order:
  if it depends on an unchecked task, say so and stop.
- After finishing, mark it `- [x]` in that spec's `tasks.md`.
- Do not run a second agent (Kiro, Cursor, etc.) on the same working tree at the same time.
- Do not commit, push, or open a PR unless the user explicitly asks.
  When they do, follow `.kiro/steering/git-workflow.md`.

## Layout

```
.kiro/specs/<spec>/        # requirements, design, tasks (Kiro spec)
.kiro/steering/            # project rules, loaded only when the task needs them
scripts/task.sh            # extracts one task block: ./scripts/task.sh <spec> <ID>
docs/FIX44Entrypoint.xml   # B3 FIX 4.4 data dictionary
docker-compose.yml         # Kafka, RabbitMQ, PostgreSQL+TimescaleDB (local)
docker/postgres/init.sql   # hypertable de mensagens FIX
```