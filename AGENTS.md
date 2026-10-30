# NexusRouter — Agent Instructions

This repository is spec-driven. **`.kiro/` is the source of truth** for both Kiro and Cursor. Do not copy steering or spec content into `.cursor/rules/` or skills.

## What to read before implementing

Read only what the current task needs. Do not load the full spec or every steering file up front.

1. Find the next unchecked task in `.kiro/specs/nexusrouter/tasks.md` (search for `- [ ] T-`) and read only that task block.
2. Read only the sections listed in that task's **Entradas** (`design.md`, `requirements.md`).
3. Read `.kiro/steering/coding-standards.md` when the task writes or changes C#.
4. Read `.kiro/steering/b3-guidelines.md` when the task touches FIX, session, parsing, routing, or B3 fields.
5. Read `.kiro/steering/git-workflow.md` only when the user asks for commit, push, or a pull request.

## How to execute work

- Implement **one** unchecked task from `.kiro/specs/nexusrouter/tasks.md` at a time, in phase order (T-01 → T-43).
- After finishing a task, mark it `- [x]` in `tasks.md`.
- Do not run a second agent (Kiro or Cursor) on the same working tree at the same time.
- Do not commit, push, or open a PR unless the user explicitly asks. When they do, follow `.kiro/steering/git-workflow.md`.

## Layout

```
.kiro/specs/nexusrouter/   # requirements, design, tasks (Kiro spec)
.kiro/steering/            # project rules, loaded only when the task needs them
docs/FIX44Entrypoint.xml   # B3 FIX 4.4 data dictionary
docker-compose.yml         # Kafka, RabbitMQ, PostgreSQL+TimescaleDB (local)
docker/postgres/init.sql   # hypertable de mensagens FIX
```
