# Home Lab catalog files

These JSON files are the editable Home Lab catalog defaults shipped with LMS.

The running Community Edition copies them on first start to:

```text
$LMS_DATA_ROOT/home-lab-catalog/
```

For the standard CE installation that is `/var/lib/linuxmadesane/ce/home-lab-catalog/`.
Edit the copies in that directory to change an existing app or recipe, or add a new item. LMS checks the files when the catalog is used, so a service restart is not normally required.

Files:

- `apps.json` — Docker images, ports, volumes, environment, configuration fields, health checks, and exposure rules.
- `recipes.json` — deployable app combinations, storage roles, routing relationships, and service dependencies.
- `prompt-recipes.json` — Home Lab AI recipes. Use `baseRecipePrompt`, `technicalGuidance`, `appsBeingDeployed`, and the optional connectivity fields.

User entries with an existing ID replace the built-in entry. New IDs are added to the catalog. App and recipe IDs use ASCII letters, numbers, hyphens, and underscores. Prompt recipes can reference only installable app IDs from `apps.json`.
