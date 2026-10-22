# 0005. Alert rules per device and channel, falling back to the platform's

Status: accepted

## Context

Alert rules were configuration: one list for the whole platform, by metric. That cannot express what a product built on
Pulse needs. Two plants in two pots want different soil moisture ranges, and one of two zones of a pot may be allowed to stay
quiet longer than the other. The rules have to be set over the API, per device and channel, while a device that never had any
keeps behaving exactly as before.

## Decision

- Rules are stored per device and channel in `device_rules`: a threshold rule (min, max, margin) and a no-data rule
  (timeout), at most one of each kind per channel, and only for channels the device declared. The database repeats what the
  validator checks (the shape of each kind, a non-negative margin, the foreign key to the channel), so a rule that makes no
  sense cannot be stored by whatever writes it.
- **A channel with a rule of its own uses only that. A channel without one uses the platform's rules for its metric.**
  Merging the two would make "what applies to this channel" depend on two places, and make a custom range unable to be
  wider than the default. A consequence worth knowing: a soil moisture channel has no alert until it is given a rule,
  because the platform has no default for it.
- A replace is **all or nothing**: the whole list is validated, then deleted and inserted in one transaction. An empty list
  clears the device's rules.
- Rules are read for every batch of readings, so a change applies to the next reading. That is one indexed query per batch.
- **An alert belongs to a rule by name, and the name carries the range**, so editing a range makes a different rule.
  Without care the alert raised under the old range would stay raised for ever, because nothing evaluates it any more, and a
  restart would restore it. So when `AlertTracker` sees the first reading of a channel in a batch, it resolves the active
  alerts of that channel whose rule is not among the rules now in force. The same covers a rule removed outright and a
  changed platform configuration.
- No-data rules are applied in the silence query itself: a channel is silent when it has been quiet longer than its own
  timeout, or the platform's if it has none. The alert's rule name carries the timeout that applied.
- Only service callers write rules, and owners can read them. Greenhaus turns a pot's requirements into rules and decides
  what a tier may have. Pulse would bypass that if a user could write rules to it directly.

## Alternatives considered

- **Give each rule an id, and key alerts by id.** Stable across edits, but then the alert history loses the range that was
  breached, and every rule needs an identity the contract does not have. Resolving by name keeps the history readable.
- **Evaluate rules in the database.** Possible, but then hysteresis state would live in SQL as well. The tracker keeps one
  place that decides.
- **Merge custom and default rules.** Rejected above.

## Consequences

- There is no way to switch alerts off for a channel that has a platform default, short of a very wide range.
- An alert on a channel whose rule changed is resolved when that channel next reports. A channel that never reports again
  keeps its raised alert, and the no-data alert covers that case.
- Changing the no-data timeout of a channel that is already silent resolves the old alert and raises a new one under the
  new name, so the history shows both.
- A rule on a retired device cannot be changed (`409`), and can still be read.
