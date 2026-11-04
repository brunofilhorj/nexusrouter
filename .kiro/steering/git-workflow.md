---
inclusion: manual
name: git-workflow
description: Trunk-based development e execução interativa de tasks de uma spec, incluindo branch curta a partir de main, commits confirmados pelo usuário, push e abertura de PR.
---

# Git and Task Execution Workflow Guidelines

Você (Kiro, Cursor ou outro agente) só deve iniciar ações e interações com o Git quando o
usuário solicitar explicitamente a execução de tasks de uma spec em
`.kiro/specs/<spec>/tasks.md` (ex.: `nexusrouter`, `dropcopy-simulator`).

Antes de executar qualquer ação, siga rigorosamente esta máquina de estados e regras:

## 1. Verificação Inicial de Branch

O repositório usa **trunk-based development**. A única branch de longa duração é `main`.
Não existe `develop` nem branch `release/*`. Release é uma tag semver criada em `main`
(ver `README.md`).

- ANTES de iniciar a execução da primeira task da branch, verifique a branch atual
  (`git branch --show-current`).
- O trabalho acontece em uma branch curta, criada a partir de `main` atualizada:
  `feature/*`, `bugfix/*` ou `chore/*`.
- Se a branch atual for `main`, `master` ou `develop`, pause imediatamente, informe o
  usuário e pergunte se deseja criar uma branch curta antes de continuar.
- Pull requests apontam sempre para `main`. Não abra PR para outra base e não crie
  branch de release.
- Não crie nem faça push de tags (`vX.Y.Z`, `vX.Y.Z-rc.N`) a menos que o usuário peça.
  Promoção de ambiente é operação humana, documentada no `README.md`.

## 2. Convenção de Commits e PR

- Um commit por task. Mensagens de commit e descrição de PR em pt-BR.
- Formato: `tipo(escopo): descrição curta`, com o ID da task na mensagem.
  Exemplo: `feat(dropcopy): implementa B3FixMessageWriter (T-05)`.
- Tipos: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`.
- Commits só com os arquivos da task. Não inclua arquivos temporários nem mudanças
  alheias à task.

## 3. Execução das Tasks e Commits Interativos

Para cada task executada:

1. Implemente a alteração solicitada pela task.
2. Após concluir a task, **NUNCA** faça o commit de forma autônoma.
3. Apresente o resumo das alterações, a sugestão de mensagem de commit (conforme a
   seção 2) e **pergunte explicitamente**:
   *"Deseja que eu realize o commit com esta mensagem? (S/N)"*.
4. Só execute `git commit` após a confirmação explícita do usuário.

## 4. Verificação de Conclusão e Push

- Após o commit da última task do escopo da branch (normalmente uma fase), pergunte:
  *"Todas as tasks desta fase foram concluídas e comitadas. Deseja que eu faça o push
  da branch para o repositório remoto? (S/N)"*.
- Se ainda restarem tasks no escopo da branch, não pergunte pelo push.
- Só execute `git push` após o aval do usuário.

## 5. Abertura de Pull Request (PR)

- Logo após o sucesso do `git push`, pergunte:
  *"Deseja que eu abra a Pull Request para esta branch? (S/N)"*.
- Se confirmado:
  1. Crie um arquivo markdown temporário (`.kiro/pull-request-context.md`) com um
     resumo estruturado do propósito das alterações e dos detalhes técnicos
     implementados, com base no `tasks.md` da spec.
  2. Apresente o conteúdo ao usuário e use `gh pr create` **contra `main`**, com título
     e descrição em pt-BR, usando o arquivo como corpo.
  3. Após abrir a PR, apague `.kiro/pull-request-context.md`.