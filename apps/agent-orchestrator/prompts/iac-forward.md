# IaC Forward Generation Prompt

Generate {{FormatUpper}} infrastructure-as-code for the following Azure cloud recommendation.
Scope boundary: produce declarative IaC only for the specific recommendation context below.
Capability mode: generated_not_applied.

Recommendation context:
Action type: {{ActionType}}
Target resource: {{ResourceName}}
Current SKU: {{CurrentSku}}
Target SKU: {{TargetSku}}
Target region: {{TargetRegion}}
Estimated cost impact: ${{EstimatedMonthlyCost}}/month
Confidence score: {{ConfidenceScorePercent}}%

Requirements:
Output only valid, deployable code (no explanation outside code comments).
Include descriptive inline comments explaining intent and safety checks.
Prefer idempotent declarations and stable API versions.
Include rollback-safe guards where possible.
Begin output with a fenced code block: ```{{Format}}.
Do not include secrets, credentials, tokens, or connection strings.
Do not include imperative command execution (az, kubectl, bash, powershell).
If required values are missing, emit explicit parameter placeholders rather than guessing.
Do not generate out-of-scope resources not implied by the recommendation.

