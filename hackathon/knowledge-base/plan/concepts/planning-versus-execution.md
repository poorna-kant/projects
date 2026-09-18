# Planning versus Execution

*Illustrative example. Generic, industry-standard concept written for this public demo. Original
prose — not a quotation from any standards body, certification syllabus or published dictionary.*

Everything upstream of a released order is a **statement of intent**. Everything downstream is a
**record of what happened**. The two behave so differently that treating them the same way is one
of the more reliable ways to corrupt a knowledge base.

## A plan is replaced; an outcome is not

A plan has versions. A newer version supersedes an older one, and summing across versions
double-counts — which is why nearly every planning measure needs a version filter to mean
anything.

An outcome has no versions. A build that completed, completed. A delivery that arrived, arrived.
Applying a plan version filter to execution data would quietly delete history that a later plan
disowned, and that is precisely the history worth keeping: the record of what the organisation did
when it believed something it no longer believes.

This is why the execution tables in this demo carry **no `plan_version` column at all**. It is not
an oversight. It is the shape of the thing.

## Adherence is not achievement

Schedule attainment asks whether the plan was followed. It does not ask whether the plan was right,
and it cannot.

A build plan that was lowered to match a known shortage can attain one hundred per cent while
leaving real demand unmet. Read alone, it reports a flawless quarter. Read beside the planning gap,
it reports a quarter in which execution did everything asked of it and the business still did not
get what it wanted. Only the second reading is useful, and only the second reading survives a
question from someone who was in the room.

## The closing loop

Plan versus actual is where plan quality can finally be judged rather than asserted — and it only
works if the plan was captured and frozen beforehand. An organisation that overwrites its plans in
place can compare nothing, because the thing it would compare against no longer exists. Forecast
accuracy, forecast bias and forecast value add all rest on this, which is why immutable versions
are a governance rule and not a technical preference.

## Excess is usually an upstream consequence

Inventory held beyond foreseeable need is typically the downstream result of a plan that was raised
and then lowered after supply committed. Charging it to the execution function is common, tidy, and
wrong: the decision that created it was made months earlier, by someone else, and the record of
that decision is the only thing that can show it.

## Related

- [Plan of Record](plan-of-record.md)
- [Snapshot and Reproducibility](snapshot-and-reproducibility.md)
- Measures: `execution.build.attainment`, `planning.gap`
- Objects: `plan/objects/demo-build-order`
