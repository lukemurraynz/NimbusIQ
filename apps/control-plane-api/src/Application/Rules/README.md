# Recommendation Rule Pack

Set `ATLAS_RULE_PACK_PATH` to a JSON file (schema shown in `atlas-recommendation-rule-pack.v1.sample.json`) to override embedded rule seeds without code changes.

Behavior:
- If the file is valid and has enabled rules, it is used.
- If the file is missing/invalid, the service falls back to embedded defaults.
- Rule pack version/source are stamped into rule metadata and recommendation quality telemetry.
