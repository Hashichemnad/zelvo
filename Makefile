.PHONY: up down reset logs test test-api test-keycloak test-web e2e

up: ## Build and start the whole stack (creates deploy/compose/.env on first run)
	./scripts/dev-up.sh

down: ## Stop the stack, keep its data
	./scripts/dev-down.sh

reset: ## Stop the stack and delete its data
	./scripts/dev-down.sh --volumes

logs: ## Follow the logs of all services
	cd deploy/compose && docker compose logs -f

test: test-api test-keycloak test-web ## Run backend, Keycloak extension and frontend tests

test-api: ## Backend unit and integration tests (needs Docker for PostgreSQL)
	dotnet test --solution Zelvo.slnx

test-keycloak: ## Keycloak extension tests (needs Java 21 and Maven)
	cd src/keycloak && mvn -B verify

test-web: ## Frontend lint, type check and component tests
	cd src/web && npm ci && npm run lint && npm run typecheck && npm test

e2e: ## End-to-end tests against the running stack (run make up first)
	cd tests/e2e && npm ci && npx playwright test
