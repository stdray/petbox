# Golden schema snapshots

`{core,tasks,memory,sessions,deploy,config,log}.schema.txt` are the **committed shape of each tier's
database** — what you get by running that tier's FluentMigrator set on an empty file.
`GoldenSchemaTests` rebuilds each one on a clean temp DB and diffs it against the file here, so
**any** change to a migration body that changes the resulting schema fails a test and shows up as
a reviewable diff.

## What a snapshot contains

Built from the PRAGMAs (`table_info`, `foreign_key_list`, `index_list`/`index_xinfo`) plus the
three things PRAGMAs cannot express, taken from `sqlite_master`:

* an index's **partial predicate** (`WHERE ActiveTo IS NULL`) — the backbone of the temporal model;
* **trigger** bodies;
* **`CREATE VIRTUAL TABLE`** declarations (FTS5). Their derived shadow tables
  (`*_data`, `*_idx`, `*_content`, `*_docsize`, `*_config`) are excluded — noise.

`VersionInfo` is in the snapshot (it *is* part of the schema); its **content** — which versions
have been applied — is not.

The `config` and `log` tiers got their migrations late (their files used to be created by hand-written
runtime DDL), so their `M001` **adopts** pre-existing objects behind `Schema...Exists()` guards. The
golden here is still the FRESH shape — what the tier's migrations build on an empty file. That an
ADOPTED live file converges on the same shape is pinned separately by `LegacySchemaAdoptionTests`.

The dump is normalized, so it does **not** flip on formatting or on rewriting a migration from
raw SQL to the typed FluentMigrator API (which double-quotes identifiers and appends `ASC` to
index columns). It flips when the **schema** changes. That is the point: a migration-body refactor
that preserves the schema leaves these files byte-identical.

## Updating a golden

Only when you have **added a migration** and the schema change is intended:

```sh
PETBOX_SCHEMA_GOLDEN_UPDATE=1 dotnet test tests/PetBox.Tests --filter GoldenSchema
# PowerShell: $env:PETBOX_SCHEMA_GOLDEN_UPDATE=1; dotnet test tests/PetBox.Tests --filter GoldenSchema
git diff tests/PetBox.Tests/Data/Schema
```

Then **read the diff, line by line**. The update flag is a typist's convenience, not an approval:
the diff *is* the review artifact. If a line you did not mean to touch moved — a dropped `NOT
NULL`, a lost `ON DELETE CASCADE`, a partial index that quietly became total — the **migration** is
wrong, not the golden. Never regenerate to "make the test green".

Do not hand-edit these files.
