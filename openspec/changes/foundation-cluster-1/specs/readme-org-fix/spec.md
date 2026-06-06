# Spec — readme-org-fix

## ADDED Requirements

### Requirement: README SHALL reference 07artem132 org для всіх GitHub-pointing URLs

README.md SHALL NOT contain substring `mil-development`. Build-status badge URL (line 7) SHALL point to `https://github.com/07artem132/JSON-RPC.NET/actions/workflows/dotnet-desktop.yml/badge.svg`. NuGet feed instructions (line 60) SHALL reference `https://nuget.pkg.github.com/07artem132/index.json`.

Coverage badges (lines/methods/branches at line 4) SHALL retain relative path `.github/badges/*.svg` — їхня actual generation потребує CI workflow, що зовсім не існує у repo'і. До CI bootstrap badges renderяться як broken images; це acceptable trade-off для foundation cluster (M8 → окремий future change `ci-bootstrap`).

#### Scenario: No stale org references

- **GIVEN** post-merge state на main
- **WHEN** `grep -c 'mil-development' README.md` запускається
- **THEN** result is **0**

#### Scenario: Build badge points to actual org

- **GIVEN** README.md line 7 (build-status badge)
- **WHEN** URL extracted
- **THEN** URL matches pattern `https://github.com/07artem132/JSON-RPC\.NET/actions/workflows/.+\.yml/badge\.svg`

#### Scenario: NuGet feed instructions reference correct org

- **GIVEN** README.md instructions blok for adding NuGet source
- **WHEN** feed URL extracted
- **THEN** URL matches pattern `https://nuget\.pkg\.github\.com/07artem132/index\.json`
