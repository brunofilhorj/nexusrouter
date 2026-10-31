---
inclusion: auto
name: git-workflow
description: Trunk-based development e execução interativa de tasks do tasks.md, incluindo branch curta a partir de main, commits confirmados pelo usuário, push e abertura de PR.
---

# Git and Task Execution Workflow Guidelines

Você (Kiro ou Cursor) só deve iniciar ações e interações com o Git estritamente quando o usuário solicitar explicitamente que você execute as tasks listadas em `.kiro/specs/nexusrouter/tasks.md`. 

Antes de executar qualquer ação, siga rigorosamente esta máquina de estados e regras:

## 1. Verificação Inicial de Branch

O repositório usa **trunk-based development**. A única branch de longa duração é `main`. Não existe `develop` nem branch `release/*`. Release é uma tag semver criada em `main` (ver `README.md`).

- ANTES de iniciar a execução da primeira task de `.kiro/specs/nexusrouter/tasks.md`, verifique a branch atual do repositório (`git branch --show-current`).
- O trabalho acontece em uma branch curta, criada a partir de `main` atualizada: `feature/*`, `bugfix/*` ou `chore/*`.
- Se a branch atual for `main`, `master` ou `develop`, pause imediatamente, informe o usuário e pergunte se deseja criar uma branch curta antes de continuar.
- Pull requests apontam sempre para `main`. Não abra PR para outra base e não crie branch de release.
- Não crie nem faça push de tags (`vX.Y.Z`, `vX.Y.Z-rc.N`) a menos que o usuário peça. Promoção de ambiente é operação humana, documentada no `README.md`.

## 2. Execução das Tasks e Commits Interativos
Para cada task executada de `.kiro/specs/nexusrouter/tasks.md`:
1. Implemente a alteração solicitada pela task.
2. Após concluir a task, **NUNCA** faça o commit de forma autônoma.
3. Apresente ao usuário o resumo das alterações realizadas, a sugestão de mensagem de commit (dentro do contexto da task) e **pergunte explicitamente**: *"Deseja que eu realize o commit com esta mensagem? (S/N)"*.
4. Só execute o comando de commit (`git commit`) após a confirmação explícita do usuário.

## 3. Verificação de Conclusão e Push
- Após realizar o commit da última task pendente em `.kiro/specs/nexusrouter/tasks.md`:
  - Verifique se restam outras tasks no arquivo.
  - Caso o arquivo esteja totalmente concluído, pergunte ao usuário: *"Todas as tasks foram concluídas e comitadas. Deseja que eu faça o push da branch para o repositório remoto? (S/N)"*.
  - Só execute o `git push` após o aval do usuário.

## 4. Abertura de Pull Request (PR)
- Logo após o sucesso do `git push`, pergunte ao usuário: *"Deseja que eu abra a Pull Request para esta branch? (S/N)"*.
- Se confirmado:
  1. Crie um arquivo markdown temporário (ex: `.kiro/pull-request-context.md`) contendo um resumo estruturado da história/story, o propósito das alterações e os detalhes técnicos implementados com base no `tasks.md`.
  2. Apresente o conteúdo do arquivo gerado ao usuário e utilize a ferramenta CLI correspondente (ou oriente o comando `gh pr create`) para abrir a PR **contra `main`**, utilizando o contexto do arquivo `.md`.
