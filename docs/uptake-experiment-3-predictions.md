# Uptake Round 3: pre-registered predictions (committed BEFORE any run)

**Specimen**: generated OrderSuite, 100 handler files; 300 call sites of
OrderValidator/StrictOrderValidator.Validate (must rename) interleaved with 200
ShipmentValidator.Validate sites (same name, unrelated type, must NOT rename) plus
25 string-literal traps. 525 machine-scored markers. Output rename-invariant
(baseline md5 eb8eef48157104be9bf936aee062b96f).

**Task** (identical wording across arms): rename OrderValidator.Validate → CheckOrder.

**Arms**: O-zero (Opus, no guidance), O-adv (Opus, advisory), O-dir (Opus, directive),
S-dir (Sonnet, directive). Model pinned via --model; RoslynMcp v2 (c1b8a49) the only
MCP server.

**Predictions (2026-07-14, pre-run):**
1. O-adv: adoption remains ZERO or load-only; the agent attempts a scripted/text
   approach; nonzero errors — most likely false renames of ShipmentValidator sites
   (sed-class approaches cannot distinguish receivers) OR a DNF against max-turns
   if it hand-edits site-by-site.
2. O-zero: same or worse than O-adv.
3. O-dir: full adoption (round 2 replication), 0 missed / 0 false, and at least
   10x fewer turns than any non-tool arm that finishes.
4. S-dir: full adoption; correctness equals O-dir (the tool does the work); more
   turns than O-dir but far fewer than any hand arm.
5. If any hand arm finishes with 0/0, its turn count and cost will still exceed the
   tool arms by an order of magnitude — the economics finding survives a
   correctness null.
