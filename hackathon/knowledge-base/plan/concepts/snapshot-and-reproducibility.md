---
type: Concept
id: plan/concepts/snapshot-and-reproducibility
name: Snapshots and Reproducibility
summary: A planning number is only meaningful when paired with the moment it was captured.
domain: plan
---

ILLUSTRATIVE EXAMPLE. A generic data-governance concept, described for the public demo.

Planning data is not static. The same query, against the same system, for the same product and period, legitimately returns different answers on different days — because the plan itself changed. This is correct behaviour, not a defect.

A **snapshot** is a capture of the planning data as it stood at a point in time. Quoting a planning figure without its snapshot is like quoting a price without a currency: the number is real, but it cannot be acted on safely.

Three rules follow.

**Resolve explicitly.** A request without an as-of date should resolve to the latest available snapshot and then say which one it used. Silently defaulting is acceptable; silently *hiding* the default is not.

**Never sum across snapshots.** Two snapshots are two views of the same underlying plan, not two quantities. Adding them double counts. Within one snapshot, demand is additive across families and periods; across snapshots it is not additive at all.

**A difference is not an error.** When a figure changes between snapshots, the first question is not "which is wrong?" but "what changed in the plan, and was it approved?" Most differences are legitimate revisions.

Reproducibility is the practical payoff. Six weeks after a decision, someone will ask why a commitment was made. If the plan version and the snapshot were both recorded, that question is answerable and the decision can be defended. If only the number was recorded, it cannot, and the discussion becomes a contest of recollections.

This is why every calculated figure in this demo is returned together with the snapshot it came from, the plan version in force, and the rows that were included and excluded.
