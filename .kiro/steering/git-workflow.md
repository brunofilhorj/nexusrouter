---
inclusion: auto
name: git-workflow
description: Fluxo de trabalho Git e execução interativa de tasks do tasks.md, incluindo verificação de branch, commits confirmados pelo usuário, push e abertura de PR.
---

# Git and Task Execution Workflow Guidelines

Você (Kiro ou Cursor) só deve iniciar ações e interações com o Git estritamente quando o usuário solicitar explicitamente que você execute as tasks listadas em `.kiro/specs/nexusrouter/tasks.md`. 

Antes de executar qualquer ação, siga rigorosamente esta máquina de estados e regras:

## 1. Verificação Inicial de Branch
- ANTES de iniciar a execução da primeira task de `.kiro/specs/nexusrouter/tasks.md`, verifique a branch atual do repositório (`git branch --show-current`).
- Confirme se a branch está em um padrão adequado (`feature/*`, `bugfix/*`, `release/*`, `chore/*`, etc.).
- Se a branch atual for genérica ou incorreta (ex: `main`, `master`, `develop`), pause imediatamente, informe o usuário e pergunte se deseja criar ou mudar para uma branch apropriada antes de continuar.

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
  2. Apresente o conteúdo do arquivo gerado ao usuário e utilize a ferramenta CLI correspondente (ou oriente o comando `gh pr create`) para abrir a PR utilizando o contexto do arquivo `.md`.
