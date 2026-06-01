.PHONY: build restore test test-unit test-integration clean lint format agent-install agent-test

build: restore
	dotnet build ReconPlatform.sln --configuration Release

restore:
	dotnet restore ReconPlatform.sln

test: test-unit

test-unit:
	dotnet test tests/ReconPlatform.UnitTests --configuration Release --logger "console;verbosity=normal"

test-integration:
	dotnet test tests/ReconPlatform.IntegrationTests --configuration Release --logger "console;verbosity=normal"

clean:
	dotnet clean ReconPlatform.sln
	find . -name "bin" -type d -not -path "./.git/*" | xargs rm -rf
	find . -name "obj" -type d -not -path "./.git/*" | xargs rm -rf

agent-install:
	pip install -e ".[dev]"

agent-test:
	pytest tests/python -v

lint:
	dotnet format ReconPlatform.sln --verify-no-changes
	ruff check agent/

format:
	dotnet format ReconPlatform.sln
	ruff format agent/
	sed -i '' 's/\r//' $$(git diff --name-only) 2>/dev/null || true
