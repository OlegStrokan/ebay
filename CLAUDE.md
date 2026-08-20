# Writing Style Constraint: ASD-STE100 (Simplified Technical English)

Apply ASD-STE100 to ALL text you generate: internal reasoning, execution logs,
step-by-step plans, status updates, code comments, commit messages, and final
responses.

## Core Grammar & Vocabulary Rules

- **Sentence length:** Max 20 words for procedural sentences (instructions, actions, steps). Max 25 words for descriptive sentences (explanations, backgrounds, summaries).
- **Active voice only.** Never use passive voice.
  - Correct: "The function returns an array."
  - Incorrect: "An array is returned by the function."
- **Imperative form for actions.**
  - Correct: "Run the unit tests."
  - Incorrect: "You should run the unit tests" or "We need to run the unit tests."
- **One topic per sentence.** State only one instruction or idea per sentence.
- **Approved vocabulary.** Use clear, explicit, unambiguous verbs:
  - Use **start** (not *initiate*).
  - Use **stop** (not *terminate*).
  - Use **use** (not *utilize*).
  - Use **verify** or **examine** (not *check*).
  - Use **show** (not *display* or *indicate*).
- **Noun clusters:** Do not join more than three nouns together.
- **No ambiguous words:** Avoid vague words like *should*, *could*, *etc.*, or *appropriate*. Be exact.

## Application Scope

### Reasoning, thinking, and planning
Draft internal plans, steps, and thoughts in STE. Format step-by-step
reasoning as direct imperative commands, for example: "1. Locate the bug.
2. Modify the configuration file."

### Code and documentation outputs
Write all code comments, docstrings, commit messages, and PR descriptions in
STE.

**Exception:** Code syntax, variable names, terminal commands, and JSON/YAML
structures stay as required by programming logic. All natural language
around the code must still follow STE.
