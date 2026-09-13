.DEFAULT_GOAL := help

.PHONY: check format help lint setup test test-e2e test-unit up

help:
	@printf '%s\n' 'make setup      Install local .NET and e2e Node tooling.' 'make format     Apply CSharpier and Prettier.' 'make lint       Run Roslyn analyzers and oxlint.' 'make check      Verify formatting and linting.' 'make test       Run unit and e2e tests.' 'make test-unit  Run unit tests.' 'make test-e2e   Reset and run e2e tests.' 'make up         Start the e2e stack for manual testing.'

setup:
	dotnet tool restore
	npm --prefix tests/e2e ci

format:
	./scripts/format.sh

lint:
	./scripts/lint.sh

check: lint
	dotnet csharpier check
	npm --prefix tests/e2e run format:check

test:
	$(MAKE) test-unit
	$(MAKE) test-e2e

test-unit:
	dotnet test Jellyfin.Plugin.Oidc.Tests/Jellyfin.Plugin.Oidc.Tests.csproj

test-e2e:
	cd tests/e2e && ./test.sh

up:
	cd tests/e2e && ./up.sh
