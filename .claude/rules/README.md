# Rules

Standing operator corrections that hold in **every** session and cannot be derived from anywhere else. Every file here is loaded into context in full, every session; that budget is why the bar is high.

Before adding a rule, all four must be true. Any "no" means it belongs elsewhere:

1. True in every session? No → a skill or a design doc.
2. Underivable from code, a test, or `settings.json`? No → not a rule.
3. Stated by no other document? No → that document owns it.
4. Fits in ~10 lines without narrative? No → it is a spec; the rule is one line pointing at it.

Dates are provenance. On a scoped rule the date is load-bearing; never strip it.

- [Never act without permission](never-act-without-permission.md)
- [No contributions to external projects](no-external-contributions.md)
