# IaC Rollback Generation Prompt

Generate a {{FormatUpper}} rollback plan to revert the following Azure change.
Scope boundary: rollback content must address only the specific resource and SKU transition below.
Capability mode: generated_not_applied.

Resource: {{ResourceName}}
Revert from SKU: {{TargetSku}} back to {{CurrentSku}}

Requirements:
Include safety checks and post-change health verification as inline comments.
Keep rollback idempotent where possible.
Output only a fenced code block: ```{{Format}}.
Do not include secrets, credentials, tokens, or connection strings.
Do not include imperative command execution (az, kubectl, bash, powershell).
If critical rollback inputs are missing, emit explicit placeholders and note required operator validation in comments.

