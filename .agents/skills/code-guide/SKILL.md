---
name: code-guide
description: Author an interactive HTML code guide for a substantial PR or merged change, in the house format used by docs/*-code-guide.html (curriculum chapters, interactive labs that reimplement the decision logic, and a comprehension gate). Use when the user asks for a code guide, PR guide, or review guide for a change; do not use for trivial diffs or for decision docs themselves.
---

# Code Guide

Read [references/workflow.md](references/workflow.md) completely and follow it
as the required workflow.

A guide is a teaching artifact for reviewers and future maintainers, not a
changelog: it explains why the change is shaped the way it is, and its labs let
the reader drive the actual decision logic. Only substantial changes earn one
(multi-file design work, usually with decision docs behind it).
