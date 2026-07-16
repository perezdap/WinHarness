# Skill: example

## Purpose

Help an agent get oriented in this project quickly and consistently.

## When to use

- At the start of any new session or task.
- When the agent needs to confirm the project conventions.
- After a long pause or context compaction.

## Steps

1. Read `AGENTS.md`.
2. List `.agents/skills/` and note available capabilities.
3. List `.workflow/definitions/` and note available workflows.
4. Identify the current task and choose the most relevant skill or workflow.
5. Summarize the project conventions back to the user in one or two sentences.

## Output

A brief orientation summary covering:
- Project name and purpose
- Relevant skills for the current task
- Relevant workflows for the current task
- Any warnings or blockers discovered

## Example

> This is **MyProject**, an agent-agnostic workspace. The relevant skill for your request is `code-review`, and the relevant workflow is `default`. I will follow the `default` workflow steps: scout, plan, build.

