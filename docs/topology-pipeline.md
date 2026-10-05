# Implementation plan: arc-based topology pipeline

## Goal

Produce a **planar partition** of the landmass in which every border between two
countries is one shared arc, referenced by both neighbours, with byte-identical
coordinates. This simultaneously removes gaps and overlaps and makes TopoJSON
export fall out of the representation rather than having to be recovered from it.

The current pipeline derives each country independently from `Country_Shapes` and
then tries to repair the disagreements afterwards (`CorrectionService.Snap`,
`CorrectionService.FindGaps`). This plan replaces that with: build the border
network once from `Border_Tracers`, polygonize it, and assign the resulting faces
to countries. In that representation a gap is not a special case to detect — it is
just a face nobody claimed.

`CorrectionService.FindGaps` and the planned Voronoi work are dropped.
`CorrectionService.Snap` is replaced by the welding step in Phase 3. The
area / area-to-length thresholds survive, in a much smaller role (Phase 2c).

## Measured facts this plan relies on

Taken from `Altera_Data_Generating_Map(13).svg`. Re-run the checks in Phase 7
against each new export; the thresholds below are derived from these numbers, not
guessed.

| Property | Value |
|---|---|
| `Rim` x-extent | 6128.3 units for 360° of longitude |
| **Scale** | **1 unit ≈ 0.0587° ≈ 6.5 km at the equator** |
| Transforms in the file | none — SVG coordinates are usable directly |
| `Border_Tracers` subpaths | 1089 (1088 open, 1 closed) + 48 `<line>` elements |
| `Substate_Tracers` subpaths | 374 (all open) + 5 `<line>` elements |
| Tracer endpoints | 2926 |
| …within 0.5 units of another tracer or a country ring | 2914 (99.59%) |
| …within 1.0 units | +8 (0.27%) |
| …needing manual attention | 4 (0.14%) — two degenerate `<line>`s |
| Junction kind | T-junctions dominate: ~1500 endpoints land on another tracer's *interior*; only 152 are exactly endpoint-to-endpoint |
| Endpoints running out to the coast | 933, of which 692 lie *exactly* on a `Country_Shapes` ring |
| Drawn linework resolution | median 1.75 units between drawn points; 7% of segments < 0.5, 19% < 1.0 |
| Unclaimed corridor along tracers | 14% of sampled points, but width median 0.00, p99 0.11, **max 1.60 units** |
| Shortest legitimate `<line>` connector | 0.44 units |

Two tolerances follow, and only two:

- **`MergeTolerance = 0.5` units and `SplitTolerance = 3.0`** (≈ 3.3 km and 19.6 km) — how
  far a loose end may reach to find something to attach to, kept apart because the two
  welds carry different risk. Phase 3 has the measurements; the paragraph below is the
  history of the single figure they replaced.

  Merging stays below the 1.75-unit linework resolution, so it cannot merge genuinely
  distinct features. Splitting may exceed it, because it moves no existing vertex: it
  threads the loose end into the arc it met and leaves everything else where it was.

  **Revised upwards from 0.5 on measurement.** The 99.59% figure in the table above
  was measured against `Country_Shapes` rings, but the graph's coast is the *union*
  boundary, which sits slightly off those rings wherever shapes overlapped. Against
  the real coast, the residue is larger and the tolerance has to cover it:

  | tolerance | ends merged | ends split onto an arc | **left loose** |
  |---|---|---|---|
  | 0.5 | 218 | 2507 | **147** |
  | 0.75 | 234 | 2594 | **44** |
  | 1.0 | 257 | 2608 | **7** |
  | 1.5 | 287 | 2583 | **2** |

  Of the 147 left loose at 0.5, 142 had an arc within 1.0 units (median 0.67), split
  76 coast / 63 border / 8 substate — ordinary junctions falling just outside. Going
  to 1.0 raises the merge count only from 218 to 257, so union-find is not chaining
  distinct junctions together; it is picking up ends that were already isolated.

  **1.5 is where it turns, which is why 1.0 is the stopping point.** The two
  remaining loose ends cost 30 further merges *and* 25 fewer splits: ends that ought
  to attach partway along an arc are instead being merged with another endpoint. That
  is the chaining failure the tight tolerance exists to prevent, and it is visible in
  the split count falling. The three candidates beyond 1.0 sit at 1.43, 2.79 and
  12.48 and are better reported than guessed at.

  **That last conclusion was wrong, and splitting the tolerance in two is what showed
  it.** The chaining failure above is a property of *merging*, so it argued only for a
  tight merge tolerance; the whole table conflates the two welds, because at the time
  they shared one figure. Two of those three candidates, at 1.43 and 2.79, are ordinary
  T-junctions that splitting resolves safely, and each of them was costing a country.
  Only the one at 12.48 is better reported than guessed at. See Phase 3.
A second tolerance, `MouthBridgeTolerance`, was planned for welding river mouths shut.
It is not needed: see 3c, where the measurement that would have set it showed there is
nothing to weld.

## Phase 0 — groundwork

**0a. Fixed precision model.** `Program.cs:39` currently passes
`currentInstance.DefaultPrecisionModel`, which is floating. Replace with a fixed
model so noding and polygonizing are reproducible and exact:

```csharp
new PrecisionModel(10000.0)   // 0.0001 SVG units ≈ 0.65 m
```

This grid is 4400× finer than the shortest legitimate connector and 5000× finer than
`MergeTolerance`, so nothing real is quantised away and snap-rounding robustness is
unaffected by the choice.

A coarser 0.001 units (≈ 6.5 m) was tried first and rejected on measurement: it moved
52 countries' areas by more than 0.1%, worst case 3.7% on Sint Maarten, because a
fixed absolute grid costs the most on the smallest islands. At 0.0001 units only one
country moves by more than 0.1%. Both are below the current 4-decimal output
precision, so this is invisible in the written GeoJSON — but area is computed before
rounding, which is what shows the shift the written file does not.

**0b. Keep SVG coordinates through the pipeline.** `HandleAsync` currently calls
`Rescale` then `Unproject` inside the per-element `Task.Run`, so every later stage
works in degrees. Move both to the very end (Phase 6). Reasons:

- The defects being corrected are *drawing* errors. They are uniform in SVG units
  and wildly non-uniform in degrees — a fixed tolerance in degrees varies by
  roughly 10× between the equator and 85° latitude.
- Both tolerances above are only meaningful in SVG units.

**0c. Models.** Add to `Erpe.Altera.Map/Models/`:

```csharp
Node     // Id, Coordinate (mutable during welding)
Arc      // Id, StartNode, EndNode, CoordinateSequence (interior vertices),
         // ArcKind { Border, Substate, Coast }
Face     // Id, ordered (Arc, bool Reversed) ring + hole rings, Country? Owner
ArcGraph // Nodes, Arcs, Faces + lookup indexes
```

Delete `Models/Gap.cs` and `Models/Coast.cs` at the end of Phase 5; they belong to
the approach being replaced. `Models/Shape.cs` stays, but its `Polygon` setter
becomes unnecessary once faces own the geometry.

**0d. Fix `<line>` handling — do this first, it is a live bug.**
`SvgApproximationService.Approximate` (line 31) has cases for `SvgPath`,
`SvgPolygon` and `SvgRectangle` and returns `[]` for everything else, and
`HandleBordersAsync` filters `element is SvgPath or SvgRectangle or SvgPolygon`.
**All 53 `<line>` elements in the two tracer layers are therefore dropped today.**
In `Border_Tracers` those are legitimate short connectors (0.44 – 9.62 units)
joining junctions; losing them would leave holes in the arc graph exactly where the
graph needs to be connected. Add an `SvgLine` case and include `SvgLine` in the
element filters.

**0e. Replace the point-count filter with a length filter.** `ApproximatePath`
ends with `.Where(coordinates => coordinates.Length >= 3)`, which silently drops
every 2-point subpath — including legitimate straight border segments. Replace with
a total-length test, and make the semantics **collapse, not delete**:

- total flattened length `< 0.1` units → discard (kills the two degenerate
  `<line>`s at 0.010 and 0.028 units; keeps the 0.431-unit one and the 0.44-unit
  connector).
- total length `< MergeTolerance` → keep, and let Phase 3a's union-find merge both
  endpoints into one node. The arc becomes a zero-length self-loop that the noder
  drops, **but anything it was connecting stays connected through that node.**
  Deleting such an arc outright can disconnect the graph; collapsing cannot.

## Phase 1 — read the tracer layers

New `ITracerService` / `TracerService`.

1. Select descendants of `Border_Tracers` and `Substate_Tracers` that are
   `SvgPath`, `SvgLine`, `SvgPolygon` or `SvgRectangle`.
2. Flatten with the existing `SvgApproximationService` (`Tolerance = 0.01`).
   `ApproximateClosedPath` only emits the closing point on an explicit
   `SvgClosePathSegment`, so open tracer paths stay open — the name is misleading
   but the behaviour is correct here.
3. Emit one `Arc` per subpath, `ArcKind.Border` or `ArcKind.Substate`. The single
   closed `Border_Tracers` subpath is an enclave border — emit it as an arc whose
   start and end node are the same; no special handling needed.
4. Apply 0e.

Node both layers together in Phase 3, in one pass. Where a substate border
coincides with a national border the two coincident arcs must collapse to one;
building national topology first and substates against it afterwards would not
achieve that.

`ArcGraph` owns identity, through `AddNode` and `AddArc` with monotonic counters, so
that the coast arcs added in Phase 2 share one numbering with the tracer arcs and
node identifiers stay stable once welding starts removing merged nodes.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 1437 arcs — 1065 border,
372 substate — on 2873 nodes, matching an independent count of the file exactly. The
node total is one short of twice the arc count, which confirms the single closed
subpath is the only one. 26 subpaths are dropped by the 0.1-unit filter.

## Phase 1b — collapse duplicated tracers

`CorrectionService.Collapse`, run directly after the tracers are read. This is the step
Phase 1 called for above — "where a substate border coincides with a national border the
two coincident arcs must collapse to one" — generalised, because the coincidences that
actually matter here are between one border and another: 21 of the 22 pairs on the sample
map are `Border` against `Border` and only one is `Substate` against `Border`.

**Some borders are drawn twice in the source map.** One stretch is drawn as a single path
and again as two or three, or a path is simply copy-pasted — four byte-identical copies of
one 125-unit border in one place, four of a 22.8-unit border in another.

**A border drawn twice is not two boundaries, and it is not harmless.** The two copies
leave a node at the same angle, so the face traversal has nothing to tell them apart by:
it takes the twin and walks straight back along it. That encloses nothing on that side, so
the two faces that should lie either side of the border come out as **one face covering
both countries** — and because the copies are near-identical rather than identical, they
cross, so the ring is rejected as geometry and dropped, taking both countries with it.

Caught in the act on the Salia/Anzigo border, where two consecutive segments of the face
boundary are each other's reverse, 0.0007 units apart:

```
segment 2144: 3592.5091,2299.7354 -> 3592.3499,2299.6363
segment 2146: 3592.3506,2299.6367 -> 3592.5098,2299.7359
```

**What is cut out is the repeat, not the border.** The pieces either side of the cut are
left as loose ends deliberately, so that the welding in Phase 3 attaches them to the
surviving copy. Cutting without that strands the borders that ended on the duplicate: on
this map one border arrives from the north at a node that lies 0.13 units *beside* the
surviving copy without being part of it, and deleting the duplicate outright would leave
it dangling and fuse a different pair of countries instead.

Two measured thresholds:

- **`DuplicateTolerance` = 0.25 units** (in `ConvertCommand`). Of the 131,850 units of
  tracer, 1490.8 lie within 0.01 units of another tracer and those are the duplicates. The
  answer is the same anywhere between 0.10 and 0.50 — 1488 units at 0.10, 1513 at 0.50 —
  and two genuinely distinct borders 0.25 units apart would be a corridor a seventh as wide
  as the gap between consecutive drawn points.
- **`MinimumDuplicateRun` = 4.0 units.** 1551 runs come to 1.75 units or less, ten more to
  between 2.0 and 2.8, then nothing until the shortest genuine repeat at 8.6. The short
  ones are two borders leaving a junction together for a segment or two; the lattice the
  tracers sit on has steps of 1.75 and 2.0 units, so the apparent gap just below 2 is an
  artifact of the lattice and the real empty span is 2.8 to 8.6. **This was first set to
  2.0 and that was wrong**: the ends of a 2-unit run sit about 1.4 units from the arc it
  supposedly repeats, too far for welding to put back, and seven ends were left loose that
  nothing could resolve.

The shorter arc gives way, an equal pair is settled by identifier so that four copies
always leave the same survivor, and an arc kept as an original is never itself removed.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 14 stretches covering 903.3
units cut out. An independent census of the welded graph afterwards finds **no overlap run
of 4 units or more anywhere**, against 34 runs and 1491 units before. Loose ends stay at 8,
the same eight as before the step existed. Faces 7129 → 7090, noded crossings 303 → 250,
faces dropped for a self-crossing ring 5 → 4 and their area 61,230 → 27,878 square units.

**Eleven countries come back from nothing**, and the land comes out of the neighbours that
had absorbed them, in matching amounts:

| Country | Before | After | Taken back from |
| --- | --- | --- | --- |
| Anzigo | 3.3 | 820,100.2 | its own dropped face |
| Salia | 0.2 | 335,479.9 | Anzigo's fused face |
| Smogland | 2,034.1 | 105,594.9 | Selkland −103,560.3 |
| Sogdia | 0.2 | 103,186.9 | Itysia −104,920.2 |
| Manden | 0.0 | 75,237.4 | Mali −128,266.4, Nigeria −89,840.0 |
| Cape Leone | 0.3 | 53,028.9 | (same three-way redistribution) |
| Tughun | 2,590.8 | 41,897.3 | Salur −39,306.4 |
| Corubal | 0.0 | 38,426.4 | Cape Verde −38,426.2 |
| Cyrevia | 0.0 | 35,687.9 | Babria −35,687.5 |
| Evora | 0.0 | 10,269.8 | Ami −10,272.0 |
| Fiume | 1,013.3 | 8,211.0 | Dalmatia −7,198.6 |

No country loses ground without a recovery somewhere to match it, which is what makes this
a transfer back rather than a new defect. Those are the only 20 countries whose area moves
by more than 1%.

## Phase 2 — build the coastline

New `ICoastlineService` / `CoastlineService`. The country shapes are used for
exactly two things: deriving the coast, and labelling faces in Phase 5. Their
internal borders are discarded — those come from the tracers.

**2a.** Build country polygons as today: `TopologyService.Close` → `Unloop` →
`Polygonize`. The self-intersection machinery is still needed here.

**2b.** `land = UnaryUnionNG(all country polygons)`.

**2c.** Take `land`'s rings:

- **Exterior rings** → coast arcs (`ArcKind.Coast`).
- **Interior rings (holes)** → classify. A hole is either a genuine inland sea
  (keep as a coast arc — it is real coastline) or an enclosed gap between countries
  (discard — the interior is governed by tracers now). This is the one place the
  existing `FindHoles` criteria survive.

  **Classify on the area-to-length ratio alone, at 0.15 SVG units.** The area test
  from `CorrectionService.FindHoles` is dropped.

  The ratio is a hydraulic radius: it has units of length and scales linearly with
  the size of a fixed shape, so it measures thinness and smallness at once and
  subsumes an area test. Measured over the sample map's 3094 holes it is sharply
  bimodal — 2632 gaps top out at 0.0955 and the 462 lakes begin again at 0.1965, a
  span of 2.06× containing nothing at all. Any threshold between those bounds
  classifies the sample identically, which is what makes 0.15 a measurement rather
  than a tuned guess.

  The area term was kept at first, converted to 2.5 units², and then removed because
  it cannot be justified: the holes it would additionally catch form a continuum with
  no break (0.98, 2.00, 2.49, 2.58, 2.68, …), so 2.5 catches 3 of them, 5.0 catches
  14 and 10.0 catches 48, all arbitrary. Worse, those holes have areas around 1–2.5
  units², so the area term was catching *medium roundish* holes rather than the tiny
  ones it was meant for — the ratio had already caught everything small.

  `CoastlineService` warns if any hole lands inside the measured empty valley, since
  the separation is what the threshold rests on. If that warning ever fires, the
  ratio has stopped being a clean discriminator and needs rethinking, not nudging.

**2c-bis. Cut tracers back to the lake shore.** Borders are drawn over lakes rather than
stopping at them: 136 tracers in the sample map reach inside one. None crosses — the
deepest goes 0.783 units in and 113 of the 136 stay within 0.05 — but left alone they
shave slivers off the lake when the faces are traced, so a lake ends up partly owned by
the countries around it. Measured before the fix: 108 sliver faces, 751 units², 0.877%
of all lake area, across 35 lakes.

Clipping here rather than afterwards means the trimmed end is welded onto the lake ring
in Phase 3 and the lake stays a single face. 383 tracers are trimmed, removing 40.61
units of line in all, and a check afterwards reports any tracer still inside a lake.

**Clip against the lake grown by a hair, not the lake as drawn.** A border that follows
the shore sits within rounding of it rather than cleanly inside, so differencing the lake
itself leaves it in place — it then exists twice over, once as the lake shore and once as
the border, and the face traversal may pick up either. That is what left one tracer
inside a lake, and what put border arcs into the boundary of the largest lake's face so
that it came out as land. Growing by 0.001 units, about 6 m against a drawing resolution
of 1.75 units, takes those with it. A border that merely ends at the shore is pulled back
by the same hair and welded onto the ring again in Phase 3.

**2d.** Coast rings need splitting at the points where Phase 3 attaches a tracer
endpoint. Easiest to defer: feed whole coast rings into Phase 3 and let the noder
do the splitting. Each ring is therefore added as one closed arc for now.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 5241 coast shells, 462
lakes kept, 2632 holes discarded as gaps, giving 7140 arcs in total with the tracers.
The discarded holes total 25.7 units² — mean 0.0097, largest 2.09 — against 85,696
units² of kept lakes, largest 20,032. At 0.03% of all hole area the discards are
digitizing slivers, not water.

The union in 2b is the pipeline's slowest step at roughly 55 seconds for ~7300 rings.
One-off, and not worth optimising until something downstream needs the time.

## Phase 3 — weld the loose ends

Extend `ICorrectionService`, replacing `Snap` and `FindGaps`. All distances in SVG
units; all lookups through an `STRtree` over arc segments.

**The two welds have separate tolerances, `MergeTolerance` = 0.5 and `SplitTolerance` =
3.0**, because they are not equally safe. Merging moves both ends, to the middle of the
group, and chains transitively through union-find, so one generous tolerance can gather up
a whole group of ends that were never meant to meet. Splitting threads the end into the arc
it met as a node and moves no existing vertex, so a wrong split costs a kink in one arc and
nothing else. Merging also runs first and consumes what it resolves, so the lower the merge
tolerance, the more of the work falls to the safer of the two.

Measured by running the whole map at each combination and counting the countries that come
out with any geometry at all:

| merge | split | merges | splits | ends left loose | faces | countries | dropped area |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1.0 | 1.0 | 425 | 2617 | 8 | 7090 | 1199 | 27,878 |
| 1.0 | 3.0 | 425 | 2624 | 1 | 7097 | 1201 | 27,878 |
| 0.75 | 3.0 | 400 | 2649 | 1 | 7106 | 1205 | 18,816 |
| **0.5** | **3.0** | **386** | **2663** | **1** | **7110** | **1209** | **18,816** |
| 0.5 | 5.0 | 386 | 2663 | 1 | 7110 | 1209 | 18,816 |
| 0.5 | 1.0 | 386 | 2656 | 8 | 7103 | 1207 | 18,816 |
| 0.25 | 3.0 | 355 | 2692 | 3 | 7111 | 1206 | 18,816 |

**One loose end costs a country.** That is why eight warnings mattered far more than eight
sounds: an unresolved end is a hole in the border network, and a hole means the two
countries either side of it fuse into one face which only one of them can own. Resolving
seven of the eight recovers six countries on its own.

**Splitting at 3.0 is insensitive.** Of the eight ends loose at 1.0, seven have their
nearest arc between 1.0 and 2.79 units away and the eighth at 12.48, so any tolerance
between 2.8 and 12.4 resolves the same seven — 3.0 and 5.0 give identical output down to
the face count. The remaining end is a drawing error to be fixed in the map, not reached
for.

**Merging at 0.5 is a peak, not a plateau**, and is the weaker of the two figures. The 39
merges given up between 1.0 and 0.5 were wrong ones: ends that should have attached partway
along an arc, merged with another endpoint instead — the failure this plan predicted at 1.5,
which was already happening at 1.0. Below 0.5 it turns: at 0.25 three pairs that genuinely
meet can no longer find each other and the enclosed land area rises by 2430 units, which is
a boundary closing where it should not.

Seventeen countries come back from nothing at 0.5/3.0, among them Shona (416,678), Chapada
Diamantina (335,453), Ladakh (282,486), Madiyan, Parasia, Menominee, Pajistan, Tebul,
Lawan, Kashin, Duran, Colchia and Zetna — and the land comes out of the neighbours that had
absorbed them, Kalahari −423,991, Toncantins −335,464, Wutsang −282,596. These are cases
this plan had put down to missing tracers; most of them were a border end falling one to
three units short of the arc it was drawn to meet.

**The cost of the gap between the two figures** is that two ends facing each other across
a distance between 0.5 and 3.0 units are no longer merged but resolved by splitting one
onto the other, which leaves a short reversed stub where they meet. It was expected to be
the expensive part of lowering the merge tolerance and it is not: arcs shorter than a unit
go from 171 to 182, five of the eleven being bare two-point ones, while arcs shorter than
half a unit actually fall from 116 to 105. Set against 9062 units of face area recovered,
that is a good trade, but it is the thing to look at first if a future export's numbers
move the wrong way.

**One country is lost to it:** Winnebagee, 4,122 square kilometres, whose face goes to
Wisconsin, the substate that encloses it. That is the enclave-ownership weakness — a single
interior point decides who owns a face — rather than anything about the weld, and it is the
same weakness that hides the fused countries from 5b.

**3a. Endpoint ↔ endpoint, with union-find.** Group endpoints within `MergeTolerance` into
one node using a disjoint-set structure, then set the node's coordinate to the group's
centroid. Union-find rather than pairwise snapping because the old `Snap` sorts by X,
`break`s after the first partner found, and therefore resolves neither chains nor
transitivity: A→B and B→C can leave A and C apart. 386 of them, and it absorbs the
short-connector case from 0e.

**3b. Endpoint ↔ segment interior — the dominant case.** For each endpoint still
unattached, find the nearest arc segment within `SplitTolerance`. Split that segment at the
foot of the perpendicular, insert a node there, and merge the endpoint into it. 2663 of them
in this file. **Do not treat this as the exception path** — it is how most of this map's
borders meet, by seven to one.

**3c. River-mouth weld — dropped. Not needed for this input.**

Two separate findings retired this step.

*The original premise was wrong.* It said to weld `P` to the nearest point on a ring
*other* than its own. That does not survive Phase 2: because the coast is the
boundary of the **union** of the countries, the two walls of a gap open to the sea
are not two rings. They are one ring, walked in along one wall and back out along the
other, so a different-ring test could never fire on the case it was written for.

*The corrected version then found nothing to do.* The real signature of a river mouth
is a **narrow neck**: two points close in space but far apart along the boundary,
where "far apart along the boundary" has to mean `min(forward, perimeter - forward)`.
A one-way walk simply returns to its own starting point, which makes every point on
every ring look like a neck — an error caught only by running a control. Measured
correctly over the 1024 coast landings, at a ring separation of just 5 units, enough
to catch a notch only 2.5 units long:

| closest the ring returns | landings |
|---|---|
| < 0.5 units | **5** |
| < 1.0 units | 6 |
| < 2.0 units | 30 |
| the next one out is 1.17 units | the other 1018 |

A control taken 150 units along the same ring, away from any landing, returns close
*more* often than the landings do — 0.8% against 0.4% within 2.0 units at separation
over 40. So the few narrow necks that exist are ordinary coastal geography, isthmuses
and straits, and they are not associated with borders reaching the sea.

Gaps open to the sea turn out to be handled already: either the two walls fell within
the snap and union tolerances and closed when the countries were unioned, or the gap
was enclosed and 2c discarded it as a hole. Implementing 3c on this input would weld
shut genuine coastal features and fix nothing.

Worth re-running the same measurement against a new export before assuming this still
holds. If necks do appear, the corrected formulation above is the one to build, with
a tolerance capping how wide a neck may be closed.

**3d. Report, do not guess.** Any endpoint unresolved at `SplitTolerance` goes to a
warning list with its SVG coordinates and, so the report says how far short it fell
and what it probably should have met, the kind and distance of the nearest arc beyond
the tolerance. Expect 1 entry, the end whose nearest arc is 12.48 units away. This list is
the manual-review queue for each new export, and it must stay short — if it grows,
something changed upstream. Each entry is worth a country, so none of them is cheap.

**3e. Apply the tolerance to endpoints only, never to interior vertices.** The
linework interiors are not defective; only the ends are. This keeps every coast and
border vertex bit-identical to the drawing, and it is why welding is done as a local
operation rather than by snap-rounding the whole coast: a global grid at either
tolerance would staircase the 19% of drawn segments shorter than 1 unit.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 386 ends merged onto each other
within 0.5 units, 2663 split an arc to attach partway along it within 3.0 — 700 of those on
the coast — and 1 remains loose and reported. T-junctions outnumber endpoint-to-endpoint
joins by seven to one, so splitting is the main path, not the exception.

## Phase 4 — node and trace faces

**Faces are traced through the arc graph, not rebuilt with `Polygonizer`.** This
departs from the original plan, which said to run `SnapRoundingNoder` and then
`Polygonizer`. Both return fresh geometry — `LineString`s and `Polygon`s — with no way
to carry `Arc` identity through, so the arcs would have to be recovered afterwards by
comparing coordinates. Since "both neighbours reference the same `Arc`" is the
guarantee the whole design rests on, it is not something to discard and rebuild.

**4a. Node the crossings.** 400 places remain where two arcs cross away from any node,
which a traversal cannot represent: each arc would be traced as though the other were
not there and the faces around the crossing would overlap. Each crossing is split in
place, both arcs taking the same new node. An arc crossing *itself* counts — 166 of
the 400 are self-crossings, and they break a traversal exactly as badly.

**4b. Trace the rings.** At each node the outgoing half-edges are sorted by the
direction they leave in. Arriving along a half-edge, the ring continues along the next
half-edge **clockwise** from the way back, which keeps the enclosed area on the left.
Bounded faces come out anticlockwise with a positive signed area; the outer boundary of
each separate piece of land comes out the other way with a negative one.

**4c. Assign the outlines.** Each negative ring is the outside of one piece of land, so
it belongs as a hole to whichever face encloses it, and to the ocean if nothing does.
Containment must be tested with **`Contains`, not `Covers`**: an island's outline is its
own shell traversed the other way round and shares every vertex with it, so `Covers`
reports the shell as enclosing its own outline and no island ever reaches the ocean.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 400 crossings noded —
matching an independent count exactly — and 7182 faces traced with 439 holes, 5188
outlines going to the ocean.

Two invariants are checked in the run and both hold:

- **Every half-edge lies in exactly one ring.** This is what "the faces tile" means,
  and it is checked directly rather than inferred from areas. It is the first thing to
  look at if a face ever comes out wrong.
- **The face areas account for the land.** Faces cover 5,834,924 square units against
  the 5,769,621 the coastline enclosed. The 65,303 excess is the 23 lakes (462 kept
  minus 439 holes) that a border crosses: splitting such a lake's ring means the land
  around it routes through the lake rather than carrying it as a hole, so the lake's
  halves become faces in their own right. A total running *under* the land area would
  mean a face had failed to close, and is the failure to watch for.

Those 23 lake faces are the one thing Phase 5 has to handle deliberately: they are
water, but they sit inside a country shape and so will label as land unless the shapes
exclude their lakes.

## Phase 5 — assign faces to countries

**5a. Water is told from land by what bounds a face, not by how it nests.** Lake shores
are carried as their own `ArcKind.Lake`, kept apart from the sea coast, and a face the
lake shore alone bounds is water. Anything with a border or a sea coast in it is
territory.

Nesting was tried first and does not work, for two separate reasons:

- **A lake with a border ending on its shore is not enclosed at all**, topologically. The
  land faces around it take the lake shore into their own boundaries, so no face carries
  the lake as a hole and there is nothing for it to be inside of. That is 126 of the 462
  lakes, including every large one — 82% of all lake area. No amount of fixing the
  containment test helps, because the ring it would look for does not exist.
- **An enclave is land enclosed by land**, so depth parity calls it water, and everything
  nested inside it is inverted too.

Provenance has neither problem and needs no geometry at all. 455 of 462 lakes come out as
water, against 336 under nesting.

**Lakes are left unowned.** They are water, not territory. With the tracers clipped in
Phase 2 each lake is a single face, so there is nothing to divide.

**5b. Faces are matched to countries, not to shapes.** A country is made of many shapes
and they routinely overlap each other, so counting shapes reports almost every face as
belonging to two countries at once — 724 of them. The shapes must be grouped by country
first.

**The national and substate layers are separate universes.** A substate shape lies
entirely inside its country's shape, so passing both to one pass reports every substate
face as fused: 723 faces. Only `UnitaryState` and `FederatedState` go into this pass.
Substates subdivide the same faces again and need a pass of their own, which Phase 6
needs anyway since it writes the two layers separately.

**A fused face needs an area threshold.** Country shapes overlap slightly all along
every border they *do* share, leaving slivers of almost no area inside both at once. 17
faces lie in two countries; 15 of them have an area of 0.0. Only a face of real size
means a tracer is genuinely missing, so the report is limited to those above 25 units².

**5c. Unclaimed land faces are absorbed** into the neighbour they share the most border
with. This is the whole of gap filling in this representation.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 6541 faces assigned to
countries, 415 left as water, 160 of 168 unclaimed faces absorbed, and exactly **two**
genuine missing tracers reported — Martinique/Caribbea at 57.6 units² and
Caribbea/Iouanorre at 27.3, both small Caribbean islands.

Absorption runs to a fixpoint, because a run of unclaimed faces can be several deep: the
innermost only gains an owned neighbour once the one outside it has been absorbed. That
took the unclaimed count from 168 to 49.

Water comes to 465 faces covering 85,710.6 square units, against the 85,695.5 of lake the
coastline measured — a difference of 0.018%, so every lake is accounted for. The run logs
both figures side by side, which is the check worth watching.

One residue remains, reported rather than papered over: **8 faces are claimed by no
country and border none**, so nothing can absorb them. They are unclaimed land whose every
neighbour is also unclaimed or water.

## Phase 6 — assemble and write

**6a. Each country's geometry is rebuilt from the faces it was assigned**, replacing the
shapes it was drawn with. From here on a border exists once and both countries either
side of it are built from the same arc coordinates.

A country holds several faces wherever a substate border cuts through it, so the faces
are dissolved back together. Unioning geometry that already shares its edges exactly
follows those edges rather than recomputing them, so the borders with the neighbours come
through untouched.

**Face boundaries have to be unlooped first.** A face boundary may return to a node it has
already passed through, where the face is pinched to a point or a hole touches its shell.
That is one ring in the graph but it crosses itself as geometry, and
NetTopologySuite rejects it. Cutting the ring at every such point into the lobes it is
really made of took the losses from **131 faces covering 1,363,675 square units — 23% of
the land — down to 4 faces and 27,878**. This is the same unlooping the drawn shapes get
in 2a, for the same reason.

(The count was quoted as 8 for a while, because what was counted was invalid lobes rather
than faces and three of these faces produce two invalid lobes each; the area never changed.
It then came down from 5 faces and 61,230 units to 4 and 27,878 when Phase 1b recovered
Anzigo.)

**6b.** Unprojection is memoized per coordinate, as set up in Phase 0b, so both sides of a
border receive literally the same output values.

**6c. Output precision is six decimals, not four.** Four decimal degrees is about 0.0017
SVG units, some seventeen times coarser than the noding grid, so distinct nodes would land
on top of each other and leave degenerate rings. Sharing itself survives either way, since
identical values round identically; what four decimals costs is degeneracy.

**One pass per layer.** A substate shape lies inside its country's shape, so the national
pass is assigned and assembled before the substate pass runs — a face carries one owner.
**Absorption runs only for the national layer**, where the countries are meant to cover
everything between them. Left on for substates it pulled 1019 faces, whole foreign
countries among them, into whichever substate happened to border them.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 6087 shapes assembled for 1199
countries from the national pass, 706 for 1544 substates, and all five GeoJSON outputs
written.

Still to do:

- **4 faces covering 27,878 square units still trace to a ring that crosses itself** and are
  left out, owned by Avanarsuaq, Kalimantan, Thelemark and Epsumba. Unlooping does not reach
  these, so they are not simple self-touches. **The cause is not duplicated tracers**: the
  census of Phase 1b was run again inside the window of each of these four faces and found
  no overlap run of even 2 units, so the guess recorded here before — collinear overlap
  along the sea coast — is wrong and the cause is still unknown. The one of the five that
  *was* a duplicate, Anzigo's, is recovered. Epsumba's face is fifteen points at a single
  coordinate and Thelemark's unloops into eleven lobes, which suggests more than one defect
  among the four. They are written out on their own by `--debug-svg`, below.
- **6d, arc-based simplification.** `VisvalingamSimplificationService` still simplifies each
  polygon independently, which gives the two sides of a border different geometry and
  undoes the guarantee. The `-low` and `-medium` outputs are affected.
- **6e, TopoJSON.**
- **The exact-sharing check in Phase 7 has not been run.** Until it has, assembling by
  union rather than by walking the arcs is an assumption, not a verified property.

## Intermediate output for inspection — `--debug-svg`

The stages between the input and the GeoJSON are not otherwise visible: a face is a list
of directed arcs and a weld is a node identity, neither of which can be looked at. `convert
--debug-svg` writes four SVG files beside the output, named after it, **in the coordinates
of the source map and with no projection applied**, so each one can be opened on top of the
file it was read from.

| File | Stage | Contents |
| --- | --- | --- |
| `<out>-coast.svg` | after 2c and 3 | `Coast` and `Lake` arcs, coloured apart, with a dot at every node |
| `<out>-borders.svg` | after 3 | `Border` and `Substate` arcs, with a dot at every node |
| `<out>-faces.svg` | after 5, national pass | every traced face as one path, shell and holes as subpaths |
| `<out>-dropped.svg` | after 6a | only the faces left out because their ring crosses itself |

Each arc carries `data-arc-id` and `data-kind`, each node `data-node-id`, and each face
`data-face-id` and, where it has one, `data-owner` — so a face or an arc named in a log
line or a warning can be found in the file by searching for its identifier.

The faces are written **between the national and the substate pass**, because a face inside
a substate belongs to a country and to a substate both and the second pass overwrites the
owner with the substate; that is the only point at which the faces carry the national
owner. A face with no `data-owner` is grey: the ocean, a lake, or ground the border network
enclosed that nothing claims. Face colours otherwise come from the identifier by golden
angle, which is only there to tell neighbours apart.

The nodes are the point of the two arc files. Welding is what the arc graph rests on, and a
node is where it decided two drawn ends were meant to be the same point.

Verified against `Altera_Data_Generating_Map(13).svg`: 6735 coast and lake arcs on 6735
nodes, 3111 border and substate arcs on 2764, all 7090 faces with 6617 of them owned, and
the 4 dropped faces. All four parse as XML, which is also the check that they are well
formed.

## Phase 7 — verification

Build these as assertions in the run, not only as tests — they are what makes each
new export trustworthy.

1. **Coverage:** every internal arc is referenced by exactly two faces; every coast
   arc by exactly one face plus the ocean.
2. **No overlaps:** pairwise face intersections are empty. Replaces the overlap
   half of `FindOverlaps`.
3. **No gaps:** the union of all faces equals `land` plus the absorbed slivers,
   with no interior holes except the lakes kept in 2c.
4. **Exact sharing:** for each pair of adjacent countries, the shared boundary
   extracted from each is coordinate-identical. This is the acceptance test for the
   whole project.
5. **Warning lists near-empty:** unresolved endpoints ≤ 12, polygonizer dangles 0,
   large faces inside two countries 0.

Unit tests go in `Erpe.Altera.Map.Tests/Services/` alongside the existing
`TopologyServiceTests`, reusing `CoordinateSequenceFactoryCustomization` and
`GeometryAssertions`. Worth covering directly: union-find chain resolution (3a),
T-junction splitting (3b), the side guard (3c), lake-vs-gap classification (2c),
and the collapse-not-delete semantics (0e).

## Risks and open questions

- **Missing tracers.** If two adjacent countries have no tracer between them they
  fuse into one face. Diagnostic 5b is the detector; there is no automatic fix, the
  tracer has to be drawn. Note `Border_Tracers` spans x 1229–5909 while
  `Country_Shapes` spans 196–6237 — the island groups outside that band correctly
  have no tracers, so the check must be area-weighted rather than a plain count.
- **Coast accuracy is now asymmetric.** Where a gap existed, the border comes from
  the tracer but the coast still comes from whichever shape was drawn further out.
  The welding in 3c closes the gap but does not reconcile which coastline was
  "right". At ≤ 1.6 units this is below the map's own precision, and shifts of this
  size are acceptable for this project — but it is a deliberate choice, not an
  accident.
- **Cost.** Phase 2b unions ~7300 rings and Phase 4 nodes ~1.9M vertices. Both are
  one-off and should be tens of seconds, but measure before optimising — and keep
  the `STRtree`s, they are what make Phase 3 linear rather than quadratic.

## Order of work

0d and 0e first — they are standalone bug fixes, independently verifiable, and
everything downstream depends on the tracer layer being read completely. Then
0a–0c, then Phases 1–2 (verifiable by writing the coast out as GeoJSON and looking
at it), then 3–4 (verified by face count and dangle count), then 5–6. Phase 6e
(TopoJSON) and 6d (arc simplification) can land after the GeoJSON output is passing
Phase 7.
