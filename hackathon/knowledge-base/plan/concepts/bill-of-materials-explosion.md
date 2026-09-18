# Bill of Materials Explosion

*Illustrative example. Generic, industry-standard concept written for this public demo. Original
prose — not a quotation from any standards body, certification syllabus or published dictionary.*

A demand plan is expressed in things a customer would recognise: a product family, a quantity, a
period. A factory cannot build any of those. It builds from parts. **Explosion** is the step that
translates the first language into the second.

The translation runs through a **bill of materials** — the structured list of what goes into a
finished product, and in what quantity. Multiply the planned quantity by the ratio for each part
and you have a **gross requirement**: the total units of that part needed if nothing were already
available.

## Gross, then net

Gross requirement is rarely the useful number, because something usually *is* already available.
**Netting** subtracts what is on hand and what is already on order, and what remains is the **net
requirement** — the quantity that still has to be found.

```
net requirement = gross requirement − on hand − scheduled receipts
```

Three choices inside that single line decide whether the answer can be trusted, and all three are
frequently left unstated:

**Does on-hand include the buffer?** Safety stock exists precisely so that it is *not* consumed by
ordinary demand. Netting against it spends the cushion on paper and reports a healthier position
than the organisation actually holds. This demo nets against total on-hand stock, including the
buffer, and says so explicitly rather than hiding the choice.

**Are scheduled receipts certain?** A scheduled receipt is a supplier's promise, not a fact. Counting
it as available is standard practice and is also the single most common source of optimism in a
supply position.

**Is the shortage floored per part?** It must be. A surplus of one component cannot substitute for
a scarcity of another — you cannot build with a spare cable in place of a missing board. Summing
signed net requirements across parts lets surplus cancel shortage and produces a number that is
arithmetically tidy and operationally meaningless.

## Why it matters for stewardship

Explosion is where a single family-level figure becomes dozens of part-level figures, and where a
shortfall stops being a number and becomes an action. "We are 150 units short on the standard
family" tells nobody what to do. "We are 150 units short because one part is 150 short against an
84-day lead time" tells somebody exactly what to do, and who to ring.

The chain only holds if every link is recorded. If the bill of materials ratio is not carried
alongside the requirement, a reviewer cannot re-derive the requirement from demand and has to take
the explosion on trust. That is a gap worth recording rather than glossing over.

## Related

- [Supportability and Gating](supportability-and-gating.md)
- [Safety Stock and Variability](safety-stock-and-variability.md)
- Measures: `planning.component.shortage`, `planning.coverage`
- Objects: `plan/objects/demo-component`
