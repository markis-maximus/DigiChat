# Tuning sprite sizes, one stage at a time

The overlay sizes every form automatically (see README "Adding real Digimon
art"). This document is about the pass that comes *after* that: a human looks
at a stage on stream and corrects what the automatic sizing got wrong. All five stages have been through it and approved by human review. What follows is
the method, kept for the next roster or the next asset source.

Run it one stage at a time. It takes a single pass per stage.

## The loop

1. A human reviews one stage in OBS and labels **every** form as one of:
   - **perfect** — this is the important one, see below
   - **too large** (they distinguish "too large" from "a bit too large", and that
     distinction shows up in the measurements, so keep it)
   - **pixelated** (again in two intensities)
2. Measure the stage, apply the two rules below, and write `scaleMultiplier`
   entries into `public/assets/overrides.json`.
3. Re-run `import-assets.bat`, refresh the Browser Source, repeat if needed.

## Why the "perfect" list is not optional

It is the only anchor. Tested against the recorded Fresh labels:

- Flagging "too large" as *area > 1.15x the stage's own median* caught **3 of
  10** and missed 7. The stage as a whole skewed large — the perfect group sat
  at 1715 area while the whole-stage median was 2058 — so a form cannot be
  judged against its own stage.
- Flagging "pixelated" as *scale >= 2.5x* caught **7 of 8 with zero false
  alarms**, because that one measures something real.

So: pixelation can be detected automatically, apparent size cannot. Never guess
at the size judgements. Ask for the list.

## Rule 1 — too large: match the perfect group's mass

The complaint is never height. Every form review called "too large" at Fresh
was *exactly the same height* as the perfect ones; they were wide sprites
carrying ~30% more area, because height is what the automatic sizing
normalises. So:

    scaleMultiplier = sqrt(medianAreaOfPerfectGroup / thisFormsArea)

This self-scales: forms called "too large" are further off and get a larger
correction than those called "a bit too large", which matched the two
intensities both times without being told which was which.

## Rule 2 — pixelated: step down the scale factor

A form's scale factor **is** its on-screen pixel size — x3 draws 3x3 blocks.
These are small sources (10-21px), so the only cure is a lower factor, which
also makes the form physically smaller. That trade has been accepted
deliberately: a form that is slightly undersized next to its stagemates reads
better on stream than one that is the right size and visibly blocky.

Step down one notch — x4 to x3, x3 to x2.5, x2.5 to x2 — preferring whole
factors, which are perfectly uniform, over halves. Do not step a form so far
that it loses more than about a third of its area; at Fresh a full step took
`fufumon` down 55% and had to be softened to a half step.

## Only one stage is ever on screen

`AppState.CurrentStage` is a single value applied to every participant, so the
overlay always renders one stage at a time. Two forms from different stages are
never visible together. Do not enforce a global size ladder between stages — an
earlier pass did, which cost crispness for no benefit. Cross-stage comparison
only happens *through time*, within one viewer's own Digimon.

## Step down the pixel ladder, but only to whole factors

x3.5 alternates 3px and 4px blocks, so it is *less* uniform than the x4 it
would replace and is not an improvement. Step x4 to x3, then x3 to x2.5, then
x2.5 to x2 — one notch at a time. Taking the lowest legal factor instead of one
notch dropped a Rookie to 40% of its stage's median area; one notch keeps the
adjusted forms in the 55-96% band, which is a visible but reasonable difference.

## The constraint that actually binds

**Every form must stay larger than its own lineage's previous stage**, in both
height and area. This is per lineage, not global: a viewer's Digimon shrinking
as it digivolves is the one thing that reads as broken. Global height bands may
overlap — after the In-Training pass, `pagumon` is 38px while the tallest Fresh
is 44px — and that is fine, because `pagumon` still towers over *its own*
Fresh (`zurumon`, 20px) and carries four times the area.

Compute the floor for each form before applying either rule, and clamp to it:

    minScale = max( (lineageFreshHeight + 3) / nativeHeight,
                    sqrt(lineageFreshArea * 1.15 / (nativeWidth * nativeHeight)) )

When the floor binds, say so in the report — it means the form cannot be
corrected as far as the review asked, and the reason is its own lineage.

## Review numbering

> **The numbering changed on 2026-08-16.** Earlier review rounds used the facing
> sheet's former hard-coded order, in which `armadillomon-family` was #1,
> `royal-base` was #3 and `agumon-family` was #30. It now follows the roster, so
> `agumon-family` is #1 and `royal-base` is #24. **Re-confirm any number quoted
> from notes written before that date** — acting on a stale number resizes the
> wrong Digimon and quietly undoes tuning that took hours.

The facing sheet now derives its order from the authoritative roster's
`orderIndex`, so the number shown by the tool, the roster, and this table stay in
sync. Translate review feedback with this table: "#24 is too big" at Champion
means `waspmon`.

**This table is a hand-maintained snapshot of `data/lineages.json`.** Adding,
removing or reordering a lineage leaves it silently wrong, and a wrong number
here resizes the wrong Digimon. Regenerate it in the same change — and never
hard-code an ordering into a tool instead; the tools read `orderIndex`.

| # | lineage | Fresh | In-Training | Rookie | Champion | Ultimate |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | agumon-family | botamon | koromon | agumon | greymon | metalgreymon |
| 2 | gabumon-family | punimon | tsunomon | gabumon | garurumon | weregarurumon |
| 3 | biyomon-family | nyokimon | yokomon | biyomon | birdramon | garudamon |
| 4 | palmon-family | yuramon | tanemon | palmon | togemon | lillymon |
| 5 | tentomon-family | pabumon | motimon | tentomon | kabuterimon | megakabuterimon |
| 6 | gomamon-family | pichimon | bukamon | gomamon | ikkakumon | zudomon |
| 7 | patamon-family | poyomon | tokomon | patamon | angemon | magnaangemon |
| 8 | gatomon-family | yukimibotamon | nyaromon | salamon | gatomon | angewomon |
| 9 | veemon-family | chibomon | demiveemon | veemon | exveemon | paildramon |
| 10 | wormmon-family | leafmon | minomon | wormmon | stingmon | dinobeemon |
| 11 | hawkmon-family | pururumon | poromon | hawkmon | aquilamon | silphymon |
| 12 | armadillomon-family | tsubumon | upamon | armadillomon | ankylomon | shakkoumon |
| 13 | guilmon-family | jyarimon | gigimon | guilmon | growlmon | wargrowlmon |
| 14 | renamon-family | relemon | viximon | renamon | kyubimon | taomon |
| 15 | terriermon-family | zerimon | gummymon | terriermon | gargomon | rapidmon |
| 16 | keramon-family | kuramon | tsumemon | keramon | chrysalimon | infermon |
| 17 | zubamon-legend-arms | sakumon | sakuttomon | zubamon | zubaeagermon | duramon |
| 18 | ludomon-legend-arms | cotsucomon | kakkinmon | ludomon | tialudomon | raijiludomon |
| 19 | gazimon-dark-dragon | zurumon | pagumon | gazimon | devidramon | megadramon |
| 20 | meramon-nightmare-soldiers | mokumon | demimeramon | candlemon | meramon | skullmeramon |
| 21 | hagurumon-machine | choromon | caprimon | hagurumon | guardromon | andromon |
| 22 | impmon-armageddon-army | puttimon | cupimon | impmon | devimon | ladydevimon |
| 23 | falcomon-savers | puwamon | pinamon | falcomon | peckmon | crowmon |
| 24 | royal-base | pupumon | puroromon | fanbeemon | waspmon | cannonbeemon |
| 25 | dorumon-x-antibody | dodomon | dorimon | dorumon | dorugamon | dorugreymon |
| 26 | ryudamon-family | fufumon | kyokyomon | ryudamon | ginryumon | hisyaryumon |
| 27 | d-brigade | bommon | missimon | commandramon | sealsdramon | tankdramon |
| 28 | kudamon-holy-beast | pafumon | kyaromon | kudamon | reppamon | chirinmon |
| 29 | liollmon-beast | popomon | frimon | liollmon | liamon | loaderleomon |
| 30 | lopmon-martial | conomon | kokomon | lopmon | turuiemon | antylamon |

## Once a stage uses the blended model, there is no median to match

Rule 1 assumes a stage's forms should all be about one size. That holds while
`stageSizeVariance` is 1. Once a stage is blended, the forms review calls
perfect legitimately span a wide range — at Rookie they run 2769 to 11118 in
area, `lopmon` and `renamon` both correct at four times each other. Deriving a
target from that group is meaningless. Apply the review's wording directly, and
take their comparisons literally: "terriermon should be modelled after lopmon"
is species knowledge no measurement will reproduce.

## Wide sprites read as large even at the right area

Two forms can carry identical area and the wider one still gets called too big —
patamon and wormmon took three passes because area-based nibbles kept saying
"close enough" while the eye was reading width. When a form is called
too big twice, cut its width rather than shaving its area again.

## Blending a stage can break growth; clamp the outliers

The lower the variance, the more the source art's own spread comes through — and
Champion's native area spans 16.5x. At variance 0.4 sealsdramon rendered
221x190, larger than its own Ultimate, and five lineages broke that way. The fix
is not a milder variance for the whole stage (that re-chunks everything); it is
to clamp the specific forms that outgrow their next stage, exactly as impmon was
clamped at Rookie. Seven clamps took the roster from seven broken digivolutions
to zero, including the two that predated any of this work.

## When the source art is the limit

Some complaints cannot be fixed by scaling at all. `fanbeemon` reads as
vertically stretched at any size because its source is 73x91 against a stage
median of 49 — the proportions are in the PNG. Say so and let a human decide
about re-exporting, rather than compensating with a multiplier that makes the
form wrong in a different way.

## What has been done

| Stage | Reviewed | Adjusted | Notes |
| --- | --- | --- | --- |
| Fresh | yes | 18 of 30 | target area 1715 (median of 12 perfect) |
| In-Training | yes | 16 of 30 | target area 2528 (median of 14 perfect) |
| Rookie | yes | variance 0.4 + 17 corrections | species sizes preserved |
| Champion | yes | variance 0.4 + clamps | one note: angemon |
| Ultimate | yes | variance 0.4 + 4 corrections | skullmeramon, lillymon, chirinmon grown |

All five stages are sized and have completed the review loop. Fresh and
In-Training use variance 1 with per-form corrections; Rookie, Champion and
Ultimate use variance 0.4. Nine forms remain on chunky scale factors because
their source art is tiny: pafumon, popomon, caprimon, dorimon, kokomon, falcomon,
lopmon, impmon, and skullmeramon. They are re-export candidates, not unresolved
automatic-scaling bugs.

Two lineages have an Ultimate that is taller than its Champion but carries
slightly less area — `armadillomon-family` (ankylomon 126x115 -> shakkoumon
99x146) and `hagurumon-machine` (guardromon 120x126 -> andromon 82x160). Both
predate the sweep. They are lean humanoids following boxy Champions, so this
may be correct; raise it during those stages' size pass rather than "fixing" it
blind.

Everything lives in `overrides.json` as one line per form, so any single
correction can be reverted by deleting its entry.
