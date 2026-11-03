// Reads Claude Code's status line JSON on stdin and prints the fields
// statusline.sh needs, one per line (empty line when missing).
const s = require("fs").readFileSync(0, "utf8");
let j = {};
try { j = JSON.parse(s); } catch {}
const g = (...path) => path.reduce((o, k) => (o == null ? o : o[k]), j);
const v = x => (x === undefined || x === null ? "" : String(x));
process.stdout.write([
  v(g("workspace", "current_dir") ?? g("cwd")),
  v(g("workspace", "git_worktree")),
  v(g("worktree", "branch")),
  v(g("context_window", "used_percentage")),
  v(g("rate_limits", "five_hour", "used_percentage")),
  v(g("rate_limits", "five_hour", "resets_at")),
  v(g("rate_limits", "seven_day", "used_percentage")),
  v(g("rate_limits", "seven_day", "resets_at")),
].join("\n") + "\n");
