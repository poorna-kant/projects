---
type: Concept
id: plan/concepts/forecast-value-add
name: Forecast Value Add
summary: Whether human adjustment actually improved on the statistical baseline, or merely changed it.
domain: plan
---

ILLUSTRATIVE EXAMPLE. A generic demand-planning discipline, described for the public demo.

Forecast value add asks an uncomfortable question: when a planner overrode the statistical baseline, did the plan get better?

The method is simple. Keep the baseline. Keep the adjustment. When actuals arrive, measure the error of both the unadjusted baseline and the adjusted plan. If the adjusted plan is closer, the judgement added value. If the baseline was closer, the intervention cost accuracy, however confidently it was argued.

Published studies of this practice consistently find that a material share of human adjustments make forecasts worse. That finding is not an argument against judgement — planners routinely know things the history cannot contain. It is an argument against *unexamined* judgement, and especially against small habitual tweaks made out of discomfort with the machine's answer rather than from genuine new information.

The governance consequence is what matters for this demo. Forecast value add is only measurable if the baseline and the adjustment are stored separately and durably. Once they are combined into a single agreed quantity and the components are discarded, the question can never be asked again. The information is not merely hard to recover; it is gone.

That is why the demo keeps `baseline_quantity` and `adjustment_quantity` as distinct fields whose sum is the planned quantity, and why the adjustment carries a stated reason. The separation is not redundancy. It is the organisation preserving its ability to audit its own judgement.

This demo records the structure that makes the analysis possible. It does not perform the analysis, because that requires actuals, and the demo dataset deliberately contains only plan.
