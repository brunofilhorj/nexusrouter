#!/usr/bin/env bash
# Uso: ./scripts/task.sh <spec> <ID>   ex: ./scripts/task.sh dropcopy-simulator T-04
spec="$1"; id="$2"
file=".kiro/specs/$spec/tasks.md"
[ -f "$file" ] || { echo "Spec não encontrada: $file" >&2; exit 1; }
awk -v id="$id" '
  $0 ~ "^- \\[[ x]\\] "id" " {p=1; print; next}
  p && /^- \[[ x]\] T-[0-9]+/ {exit}
  p && /^## / {exit}
  p {print}
' "$file"