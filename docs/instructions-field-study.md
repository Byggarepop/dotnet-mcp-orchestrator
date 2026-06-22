# What drives capability routing? (instructions-field study)

A small A/B study on this orchestrator: **does populating each capability's `instructions` field
make an agent route to the right capability/tool more often?** Blind, one-shot agents (Claude Haiku)
routed real tasks through the orchestrator under configs that differed *only* in the `instructions`
field; routing was scored from the orchestrator's own call logs.

## Findings

- **`summary` drives routing; the tool's input `schema` drives argument formatting; the
  `instructions` field is largely redundant for a capable model when those two are clear.** This is
  the right division of labor — and it's how the shipped sample config is written — but in practice
  `instructions` rarely changes behavior unless the other signals are missing.

| Condition | Result |
|---|---|
| **Clear summaries**, empty vs filled instructions | No difference — every routing trap solved with empty instructions. |
| **Opaque names + blank summaries** (instructions = only signal) | Routing breaks (brute-force scanning, mis-routes). A one-line **self-description** in `instructions` fully fixes it. Explicit "use X *instead of* Y" boundaries add nothing beyond self-description. |
| **Argument discipline** (extract a bare key from a sentence; bare class name) | Correct first try with *or* without usage instructions — the tool **schema** already conveys it. |
| **Vague summaries but real names** | Still routes correctly — the capability **name** alone carries it. |

The effect of `instructions` only became load-bearing when names *and* summaries were both stripped
of meaning.

## Practical guidance

1. **Invest in clear capability names + summaries and good tool schemas** — that's what routing and
   argument-formatting actually run on.
2. **Reserve `instructions` for conventions the schema can't convey** — a fixed tenant/auth id, a
   non-standard value format, a cross-cutting rule ("always dry-run first") — and for weaker models.
3. The cost of weak descriptions is **extra exploration and occasional mis-routes** (a capable agent
   often recovers by brute-force discovery, but pays ~2× the turns), not necessarily a wrong answer.

## Caveats

One model (Claude Haiku), small N; the effects that exist are large and mechanistically clear, the
nulls are clean but would tighten with more repeats and a weaker model.
