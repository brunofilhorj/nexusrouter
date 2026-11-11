# NexusRouter — ambiente local de desenvolvimento
#
# Sobe simulador (backend + painel), NexusRouter e ClientProbe (painel do cliente).
#   make up        # infra (Docker) + os 4 processos em paralelo; Ctrl+C encerra todos
#   make help      # lista os alvos
#
# Requer: .NET SDK 10, Node 20+, Docker, make (Windows: `choco install make`, rodando no Git Bash).

SHELL := bash
.DEFAULT_GOAL := help

SIM_BACKEND  := utils/sim/backend/src/DropCopySim.Host
SIM_FRONTEND := utils/sim/frontend
ROUTER       := src/NexusRouter.Host
CLIENT       := utils/client/src/ClientProbe

# Ordem de subida: o acceptor do simulador precisa estar de pé antes do router discar.
# A ordem só é garantida em `make up` por meio das esperas abaixo (WAIT_SIM).
WAIT_SIM ?= 5

.PHONY: help up infra down install sim-backend sim-panel router client-panel build test

help: ## Lista os alvos
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN{FS=":.*?## "}{printf "  %-14s %s\n", $$1, $$2}'

up: infra install ## Infra + simulador + painel do sim + NexusRouter + painel do cliente
	@$(MAKE) -j4 sim-backend sim-panel router client-panel

infra: ## Kafka, RabbitMQ e TimescaleDB via docker compose
	docker compose up -d

down: ## Derruba a infra Docker (os processos dotnet/node param com Ctrl+C)
	docker compose down

install: ## npm install do painel do simulador
	cd $(SIM_FRONTEND) && npm install

sim-backend: ## Simulador DropCopy: FIX :9876, API/WS :8099
	dotnet run --project $(SIM_BACKEND)

sim-panel: ## Painel do simulador (Vite) em http://localhost:5173
	cd $(SIM_FRONTEND) && npm run dev

router: ## NexusRouter.Host (aguarda o acceptor do simulador)
	@sleep $(WAIT_SIM)
	dotnet run --project $(ROUTER)

client-panel: ## ClientProbe (painel do cliente) em http://localhost:8100
	@sleep $(WAIT_SIM)
	dotnet run --project $(CLIENT)

build: ## Compila router, simulador e client probe
	dotnet build NexusRouter.slnx
	dotnet build utils/sim/backend/DropCopySimulator.slnx
	dotnet build utils/client/ClientProbe.slnx

test: ## Testes do router, simulador e client probe
	dotnet test NexusRouter.slnx
	dotnet test utils/sim/backend/DropCopySimulator.slnx
	dotnet test utils/client/ClientProbe.slnx
