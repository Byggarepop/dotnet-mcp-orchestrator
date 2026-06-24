# Feature spec: `profile` command for dotnet-mcp-orchestrator

## Context for the agent

You are adding a `profile` command (or `--profile` flag) to an existing .NET MCP
orchestrator. The orchestrator sits between an MCP client and N downstream MCP
servers, and routes tool calls progressively — it does NOT load every server's
tool manifest into context upfront. Instead it exposes a small set of meta-tools
(search / route / execute) and pulls a server's manifest into context only when
a call is routed to it.

The point of this command is to **measure the token economics of that
architecture over a session**, and specifically to report the one number no
existing tool reports: the delta between the naive "load all manifests every
turn" baseline and the orchestrator's actual progressive cost.

Existing tools (mcp-checkup, MCP Token Counter, AgiFlow/token-usage-metrics)
measure a **static snapshot** of config cost. They cannot answer "how much did
the routing architecture save me over this session, and is the routing behaving
correctly?" because that answer is a function of the session trajectory, not the
config file. That gap is the whole reason this command exists. Do not rebuild a
static config counter and call it done — the trace/delta/break-even output is the
differentiator.

## Two modes

### Static mode — `profile --config <path>`
Deterministic, no session required, CI-friendly. Reads the config, counts
manifest tokens per server, computes:
- **Resting floor**: orchestrator system prompt + meta-tools tokens (the cost
  before any work happens — nothing else reports this in isolation).
- **Naive baseline**: sum of all server manifests, the per-turn cost a flat
  config pays unconditionally.
- **Envelope**: best case (resting floor, 0 servers routed) and worst case
  (resting floor + all servers routed). Worst case is intentionally HIGHER than
  naive — orchestrated worst case means you paid the routing tax AND loaded
  everything. Reporting this honestly is a credibility requirement, not optional.

### Trace mode — `profile --trace <session.jsonl>`
Replays an actual session and produces the realized curve: per-turn active token
cost, naive comparison, load events, never-loaded savings, and break-even.

## Token counting

Default: local tokenizer (e.g. `Tiktoken` / `SharpToken`, `cl100k_base`).
Cheap, deterministic, CI-friendly. **Always print which tokenizer was used and
note cross-model tolerance (~10%).** Do not over-claim cross-model precision —
the honest benchmarks in this space all disclose tokenizer + tolerance.

Optional future mode: count against real API `usage` numbers from a live run.
More accurate, costs money, heavier harness. Not the default. Design the token-
counting layer behind an interface so a live-usage backend can be swapped in
later without rewriting the profiler.

## OPEN DECISIONS — confirm with the maintainer before implementing

1. **Sticky vs. evictable manifests.** The output below models `active_tokens` as
   monotonically increasing — once a server's manifest loads, it stays resident
   and is paid every subsequent turn. If the orchestrator actually EVICTS a
   server's tools after K unused turns, the active curve can go back down and you
   need:
   - an `eviction` event type in `load_events`
   - a non-monotonic `active_tokens` column
   Do not assume eviction exists. Ask first. If it does exist, the evictable
   story is more impressive and should be surfaced prominently.

2. **Token-counting backend.** Confirm local-tokenizer (default) vs. live-usage.
   Build local first behind a swappable interface.

## Static mode — target output

```
mcp-orchestrator profile --config orchestrator.config.json

  MCP Orchestrator — Token Profile (static)
  tokenizer: cl100k_base (Claude/Sonnet approx, ±10% cross-model)
  servers: 12 connected · 47 tools total

  RESTING STATE
    orchestrator system prompt        312
    meta-tools (search/route/exec)    161
    ──────────────────────────────────────
    resting floor                     473 tokens / turn

  NAIVE BASELINE  (all manifests loaded upfront, every turn)
    server               tools   tokens
    ─────────────────────────────────────
    github                 14    11,240
    postgres                9     6,180
    slack                  11     4,920
    filesystem              5     2,310
    sentry                  4     1,870
    … 7 more                4     3,210
    ─────────────────────────────────────
    naive total            47    29,730 tokens / turn

  ENVELOPE  (over a session — static estimate)
    best case   (0 servers routed)        473 / turn
    worst case  (all 12 routed)        30,203 / turn
    naive (the thing you're beating)   29,730 / turn  ← flat, paid every turn

  The orchestrator wins whenever a session touches fewer than 12
  servers before the routing overhead is repaid. Run with --trace
  for the realized curve on an actual session.
```

## Trace mode — target output

```
mcp-orchestrator profile --trace session.jsonl

  MCP Orchestrator — Token Profile (trace)
  session: 8 turns · 3 of 12 servers touched · tokenizer: cl100k_base

  PER-TURN  (orchestrated actual vs. naive baseline)
    turn  loaded this turn        active    naive    saved
    ──────────────────────────────────────────────────────
     1    —                          473   29,730   29,257
     2    —                          473   29,730   29,257
     3    +github (14 tools)      11,713   29,730   18,017
     4    —                       11,713   29,730   18,017
     5    +postgres (9 tools)     17,893   29,730   11,837
     6    —                       17,893   29,730   11,837
     7    +slack (11 tools)       22,813   29,730    6,917
     8    —                       22,813   29,730    6,917
    ──────────────────────────────────────────────────────
    cumulative orchestrated     114,574
    cumulative naive            237,840
    ────────────────────────────────────
    net saved                   123,266 tokens  (51.8%)

  LOAD EVENTS
    turn 3  github     triggered by route() → "create_issue"
    turn 5  postgres   triggered by route() → "run_query"
    turn 7  slack      triggered by route() → "post_message"
    9 servers never loaded — 18,610 tokens of manifest never paid

  BREAK-EVEN
    orchestrator overhead repaid at turn 1
    (resting 473 < naive 29,730; net positive from the first turn
     because this session touches only 3 of 12 servers)
```

### Column semantics (implement exactly)
- `active` = cumulative-resident tokens, NOT per-turn-loaded. Once a manifest is
  in context it stays paid every subsequent turn (under the sticky model). This
  is why `saved` SHRINKS as the session touches more servers — that erosion is
  the real story and must be visible.
- `saved` = `naive − active` for that turn.
- "N servers never loaded — X tokens never paid" is the quiet kill-shot. X is the
  sum of manifest tokens for servers never routed to. Always show it.

### Unfavorable sessions
When a session touches all/most servers early, the orchestrator LOSES (paid the
routing tax then loaded everything anyway). The BREAK-EVEN section must report
this honestly, e.g.:

```
  BREAK-EVEN
    overhead never repaid — naive would have been 1,400 tokens cheaper
    over this session; orchestrator is the wrong choice for this workload
```

Shipping the honest-failure case is what makes the favorable numbers credible.
Never suppress or soften it.

## `--format json` — schema

Clean superset of the table. Everything the human view shows is derivable from
this. `variance` is the hook for study mode (replay N times → populate cv_pct,
mean, stddev). `break_even_turn` and `orchestrator_favorable` are the fields a CI
check asserts on (gate a PR on "orchestrator stays favorable for the canonical
session" to catch regressions where a change forces an early full-manifest load).

```json
{
  "schema_version": "1.0",
  "mode": "trace",
  "tokenizer": {
    "name": "cl100k_base",
    "approximates": "claude-sonnet",
    "cross_model_tolerance_pct": 10
  },
  "config": {
    "servers_connected": 12,
    "servers_touched": 3,
    "tools_total": 47
  },
  "resting_state": {
    "system_prompt_tokens": 312,
    "meta_tools_tokens": 161,
    "floor_tokens_per_turn": 473
  },
  "naive_baseline": {
    "total_tokens_per_turn": 29730,
    "by_server": [
      { "server": "github",   "tools": 14, "tokens": 11240 },
      { "server": "postgres", "tools": 9,  "tokens": 6180  },
      { "server": "slack",    "tools": 11, "tokens": 4920  }
    ]
  },
  "trace": {
    "turns": [
      { "turn": 1, "loaded": [], "active_tokens": 473,   "naive_tokens": 29730, "saved_tokens": 29257 },
      { "turn": 3, "loaded": ["github"], "active_tokens": 11713, "naive_tokens": 29730, "saved_tokens": 18017 },
      { "turn": 5, "loaded": ["postgres"], "active_tokens": 17893, "naive_tokens": 29730, "saved_tokens": 11837 }
    ],
    "load_events": [
      { "turn": 3, "server": "github",   "trigger": "route", "tool": "create_issue" },
      { "turn": 5, "server": "postgres", "trigger": "route", "tool": "run_query" }
    ],
    "never_loaded": {
      "servers": 9,
      "unpaid_manifest_tokens": 18610
    }
  },
  "summary": {
    "cumulative_orchestrated_tokens": 114574,
    "cumulative_naive_tokens": 237840,
    "net_saved_tokens": 123266,
    "net_saved_pct": 51.8,
    "break_even_turn": 1,
    "orchestrator_favorable": true
  },
  "variance": {
    "runs": 1,
    "cv_pct": null
  }
}
```

### Field notes
- `break_even_turn`: the turn at which cumulative orchestrated cost drops below
  cumulative naive. `null` if never (unfavorable session).
- `orchestrator_favorable`: boolean; the CI assertion target.
- `variance`: when runs > 1, populate `cv_pct`, and add `mean` / `stddev` per
  turn. This reuses the CV%/mean methodology from the routing study.
- If eviction is implemented (see open decision 1), add `eviction` entries to
  `load_events` and allow `active_tokens` to decrease.

## Session trace input format (`session.jsonl`)

Define a minimal JSONL format the orchestrator can emit during a real run and the
profiler can replay. One JSON object per line, one per turn. At minimum each line
needs: turn index, any route/execute events that turn (server + tool), so the
profiler can reconstruct which manifests were resident. Keep it small; the
orchestrator should be able to write this as a side-channel log with a
`--trace-out session.jsonl` flag on the main run command.

## Suggested build order
1. Token-counting interface + local `cl100k_base` backend.
2. Static mode (resting floor, naive baseline, envelope) — deterministic, easiest
   to unit-test.
3. `--trace-out` side-channel logging on the main run command.
4. Trace mode (per-turn curve, load events, never-loaded, break-even).
5. `--format json`.
6. Variance/study mode (replay N, CV%).
7. CI assertion example gating on `orchestrator_favorable`.

## Things not to do
- Don't ship a static-only counter; the trace/delta is the point.
- Don't hide the unfavorable case or the worst-case-higher-than-naive fact.
- Don't over-claim cross-model token precision; always disclose tokenizer.
- Don't assume eviction exists — confirm first.
