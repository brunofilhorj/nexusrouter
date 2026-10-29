# NexusRouter — Agent Instructions

This repository is spec-driven. **`.kiro/` is the source of truth** for both Kiro and Cursor. Do not copy steering or spec content into `.cursor/rules/` or skills.

## Always read before implementing

1. `.kiro/steering/coding-standards.md`
2. `.kiro/steering/b3-guidelines.md`
3. `.kiro/steering/git-workflow.md`
4. The spec under `.kiro/specs/nexusrouter/` (`requirements.md`, `design.md`, `tasks.md`)

## How to execute work

- Implement **one** unchecked task from `.kiro/specs/nexusrouter/tasks.md` at a time, in phase order (T-01 → T-43).
- After finishing a task, mark it `- [x]` in `tasks.md`.
- Do not run a second agent (Kiro or Cursor) on the same working tree at the same time.
- Follow `git-workflow.md`: do not commit, push, or open a PR unless the user explicitly asks.

## Layout

```
.kiro/specs/nexusrouter/   # requirements, design, tasks (Kiro spec)
.kiro/steering/            # always-on project rules (Kiro steering)
docs/FIX44Entrypoint.xml   # B3 FIX 4.4 data dictionary
docker-compose.yml         # Kafka, RabbitMQ, PostgreSQL+TimescaleDB (local)
docker/postgres/init.sql   # hypertable de mensagens FIX
```
