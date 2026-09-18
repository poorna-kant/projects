# Supportability and Gating

*Illustrative example. Generic, industry-standard concept written for this public demo. Original
prose — not a quotation from any standards body, certification syllabus or published dictionary.*

Once demand is agreed, someone has to answer a plainer question: **how much of this could we
actually build?** That answer is **supportability**, and it is assessed before any commitment is
made to a customer.

## A product is only as buildable as its scarcest part

Supportability is not an average and it is not a blend. It is set by the **gating component** —
the one part that runs out first. Every other part can be abundant; if one is short, the plan is
capped there. This is why the constraint has so many names in practice (the limiting part, the
pacing item, the bottleneck) and why identifying it is the whole point of the exercise.

It also explains a result that looks wrong at first glance: adding supply of the wrong part
changes nothing. Supportability moves only when the gating part moves, and when it does, the
constraint jumps to whatever is next scarcest — often somewhere nobody was watching.

## What counts as supply is a decision, not a fact

Supportability looks like a measurement and is actually a series of choices:

- Is on-hand stock counted, including safety stock?
- Are scheduled receipts counted, and at what confidence?
- Is production capacity assessed, or only material?
- Is the assessment constrained by lead time, or does it assume parts can be pulled in?

Each answer makes the figure look different, and reasonable organisations answer them
differently. None of that is a problem. The problem is when the answers are not written down, and
two people quote the same supportability figure meaning two different things.

## Constrained and unconstrained

An **unconstrained** view says what the business wants. A **constrained** view says what supply can
support. The difference between them is the **planning gap**, and it is the most useful number in
the exercise — provided both sides are preserved.

Replacing the unconstrained figure with the constrained one destroys the gap. The plan then looks
perfectly achievable, because it has been quietly reduced until it is, and the unmet demand it
used to describe has no record anywhere. This is the reason immutable plan versions matter more
in supply planning than anywhere else in the cycle.

## Capacity constrains independently of material

A plan can have every part on hand and still be unbuildable for want of a line, a test cell or
people. Material availability and capacity limit separately, and their remedies share nothing: one
is a procurement problem measured in lead time, the other is an investment or scheduling problem
measured in months. Reporting a single supportable quantity without stating which constraint bound
it sends the question to the wrong team.

## Related

- [Bill of Materials Explosion](bill-of-materials-explosion.md)
- [Unconstrained versus Constrained Demand](unconstrained-vs-constrained-demand.md)
- Measures: `planning.supply.supportable`, `planning.coverage`, `planning.gap`
- Objects: `plan/objects/demo-supply`
