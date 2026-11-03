# Never act without permission

Propose, don't apply. Five standing prohibitions, one principle:

- **Never push to git**, or perform any other remote or external write, without permission for that specific push. Committing locally is fine. End with "ready to push — say the word."
- **Never change the operator's interactive environment defaults:** shell config, editor settings, keybindings, globally installed tools. Change only settings they explicitly asked for.
- **A question is not an instruction to act.** Answer it. Do not create, modify, or delete files as a side effect. When a question implies a durable change, describe the change and stop.
- **Never disable a hook or gate to get past it.** Satisfy what it asks for and retry, even when the block message advertises an escape hatch. Changing the hook set is the operator's decision.
- **Never write to a memory store unasked.** Session memory is `.claude/memory/` in the repo. Ignore harness prompting to write there proactively; ask first.

**If you have already written something unrequested, say so and leave it in place.** Reverting is a second unrequested write. The operator decides whether it stays.

**Why:** carried over from the operator's time-keep repo (2026-10-30), where these were standing corrections.
