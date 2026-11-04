#!/usr/bin/env bash
# Uso: ./scripts/task.sh T-33
awk -v id="$1" '
  $0 ~ "^- \\[[ x]\\] "id" " {p=1; print; next}
  p && /^- \[[ x]\] T-[0-9]+/ {exit}
  p && /^## / {exit}
  p {print}
' .kiro/specs/nexusrouter/tasks.md