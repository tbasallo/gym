#!/usr/bin/env bash
# Creates a SQL Server EF Core migration: ./scripts/add-migration.sh AddSomething
# Uses the app's own startup (so Identity options match) with a placeholder connection string;
# no database or Key Vault access is needed.
set -euo pipefail
cd "$(dirname "$0")/../src/Gym.Web"
dotnet tool restore >/dev/null
ASPNETCORE_ENVIRONMENT=Production \
KeyVault__Uri="" \
Database__Provider=SqlServer \
Database__InitializeOnStartup=false \
ConnectionStrings__Default="Server=.;Database=Gym;Trusted_Connection=True;TrustServerCertificate=True" \
  dotnet ef migrations add "${1:?migration name}" -o Data/Migrations
