# Measuring Supplier Delivery

*Illustrative example. Generic, industry-standard concept written for this public demo. Original
prose — not a quotation from any standards body, certification syllabus or published dictionary.*

A purchase order line carries two commitments — **a quantity** and **a date**. A supplier can honour
one and miss the other. That single fact is why supplier performance can never be a single number,
and why organisations that insist on one end up arguing about which number.

## Four readings of the same six lines

In this demo, six received order lines produce four different answers:

| Measure | Reading | Result |
|---|---|---|
| On-time delivery | Lines that arrived on or before the promised date | 66.67% |
| On-time in-full | Lines that arrived on time **and** complete | 50.00% |
| Fulfilment rate | Units received as a share of units ordered | 98.67% |
| Receipt lag | Mean signed days between promised and actual | +3.33 days |

None of these is wrong. They disagree because they count different things:

**On-time delivery ignores quantity entirely.** A line that arrived punctually with half the units
counts as on time. That is not a defect in the measure — it is a measure of punctuality, and it is
doing its job.

**On-time in-full is always equal to or harsher**, because it applies both tests to the same lines.
The gap between the two — here sixteen and a half points — is made up entirely of deliveries that
hit the date and missed the quantity. That gap is more informative than either figure alone.

**Fulfilment rate is weighted by volume**, so it is dominated by large lines and barely notices a
small one that failed completely. It looks excellent here precisely because the shortfalls were
small in units and serious in consequence.

**Receipt lag is reported signed**, so early arrivals stay visible. Goods landing far ahead of need
carry real cash and storage cost, and a measure that treats early as free encourages exactly that.
Mean and median usually differ sharply, because a handful of very late lines drag the average.

## The choices that decide the answer

Two conventions change the result materially and are routinely left unstated:

**Promised date or requested date?** Scoring against the promised date is kinder, because promised
dates can be renegotiated — a supplier who asks for three more weeks and then delivers is on time.
Scoring against the original request measures whether the business got what it asked for when it
asked for it. Both are defensible. Only one can be in force, and it should be written down.

**What is in scope?** Open lines have not yet had the opportunity to be late; scoring them as
failures punishes a supplier for an unfinished period. Cancelled lines were never going to arrive.
Excluding both is standard, and it is also a quiet way of removing the worst cases if nobody checks
why lines were cancelled.

## The point for stewardship

Quoting whichever of the four is most flattering is the ordinary abuse, and it is almost never
deliberate — people reach for the number they were shown last. The remedy is not to pick a winner.
It is to publish all four together, with the conventions attached, so that their disagreement is
read as information rather than as an error to be reconciled away.

## Related

- [Planning versus Execution](planning-versus-execution.md)
- Policy: `plan/policies/supplier-delivery-scoring`
- Measures: `execution.delivery.otd`, `execution.delivery.otif`, `execution.po.fulfilment`, `execution.receipt.lag`
- Objects: `plan/objects/demo-purchase-order`
