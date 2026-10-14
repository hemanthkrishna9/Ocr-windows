# Offline Indic OCR for Windows

The product goal, scope, architecture and targets are in `docs/product-goal.md`. That document is the source of truth.

## Agent team

The lead session plans the work and delegates it. It does not write large amounts of code itself.

| Agent | Model | Use for |
| --- | --- | --- |
| `coder` | Sonnet | Features, bug fixes, model integration, benchmarks, tests |
| `helper` | Haiku | Small mechanical tasks: boilerplate, config, docs, fixtures, searches |
| `critic` | Opus | Review of every change and plan before it is committed |

Workflow for each task:

1. The lead splits the task into independent pieces and gives each one to `coder` or `helper`. Independent pieces run in parallel.
2. The lead sends the combined result to `critic`.
3. On `BLOCK` or `APPROVE WITH FIXES`, the lead sends the findings back to a `coder` and repeats step 2.
4. On `APPROVE`, the lead commits and pushes.

## Non-negotiables

- No network calls after model download. No telemetry, no update checks, no crash upload.
- Document contents never go into logs.
- Every model, dataset and library must allow commercial distribution. Never use models extracted from other vendors' apps.
- Accuracy and speed claims need a measurement in the repo, not an estimate.
