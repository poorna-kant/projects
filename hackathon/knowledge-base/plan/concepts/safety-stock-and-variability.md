# Safety Stock and Variability

*Illustrative example. Generic, industry-standard concept written for this public demo. Original
prose — not a quotation from any standards body, certification syllabus or published dictionary.*

Plans are wrong. Demand arrives higher or lower than forecast, suppliers deliver late, yields vary.
**Safety stock** is inventory held deliberately above expected need so that ordinary variability
does not stop the build.

The word *deliberately* is doing the work. Inventory that simply accumulates because a plan was cut
after supply committed is not a buffer — it is excess, and calling it a buffer disguises a
forecasting failure as a risk decision.

## Sized against variability and exposure, not against volume

A buffer is not a flat percentage of demand. It should be sized against two things:

- **How variable** demand and supply actually are for that part. A part with steady, predictable
  consumption needs far less cushion than one that arrives in unpredictable bursts.
- **How long the organisation is exposed** — the replenishment lead time. A buffer only has to
  survive until the next delivery. The longer that wait, the more it has to survive.

A part with an 84-day lead time and a fortnight of cover is not protected. It has a fortnight of
warning, which is a materially different thing and is often reported as though it were the same.

## Quantity, time and count answer different questions

Three readings of the same buffer are all common, and they are not interchangeable:

**Quantity** — how many units are held. Useful for valuation, useless for comparison. Fifty units
is generous for one part and negligible for another.

**Days of supply** — the quantity divided by average daily consumption. Converts a number into a
duration, which is what makes parts of wildly different sizes comparable, and what lets the buffer
be read against lead time.

**Buffer coverage** — the share of parts holding at least their target. Counts parts rather than
units, on the reasoning that a build is stopped by any one missing part, not by the average part.
This makes it the right shape for a risk question and the wrong shape for a value question.

A portfolio can look comfortable on days of supply and alarming on coverage at the same time,
because one large well-stocked part can carry the average while three small starved parts are the
ones that will actually halt production. Both figures are correct; quoting only the flattering one
is the usual abuse.

## The buffer is a policy, not an observation

The quantity held is data. The quantity that *should* be held is a decision — someone chose the
service level it protects and accepted the cash it consumes. Measuring a buffer against a target
without recording who owns that target, and when it was last revisited, produces an audit of
compliance with a rule nobody can account for.

## Related

- [Supportability and Gating](supportability-and-gating.md)
- Policy: `plan/policies/buffer-target-ownership`
- Measures: `planning.buffer.days_of_supply`, `planning.buffer.coverage`
- Objects: `plan/objects/demo-component`
