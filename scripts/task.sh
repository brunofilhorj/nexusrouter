#!/usr/bin/env bash
# Uso: ./scripts/task.sh <spec> <ID>   ex: ./scripts/task.sh dropcopy-simulator T-04
# O ID também pode ser um bloco de contexto comum ("### TD-15 — ..."), ex: ./scripts/task.sh nexusrouter TD-15
spec="$1"; id="$2"
file=".kiro/specs/$spec/tasks.md"
[ -f "$file" ] || { echo "Spec não encontrada: $file" >&2; exit 1; }
awk -v id="$id" '
  !p && ($0 ~ "^- \\[[ x]\\] "id" " || $0 ~ "^### "id" ") {p=1; print; next}
  p && /^- \[[ x]\] (T|TD)-[0-9]+/ {exit}
  p && /^###? / {exit}
  p {print}
' "$file"
