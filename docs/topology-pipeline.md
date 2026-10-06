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

**2c-ter. Fill the gaps between shapes that reach the sea.** The hole test in 2c only ever
sees a gap that is *enclosed*. A gap that reaches the coast is not a hole at all: it is a
slit in the shell, and the union walks up one wall of it and back down the other. Nothing
then encloses the ground beside the slit, the border drawn along the same line is traced
into the outline of the landmass rather than into a face, and the countries either side are
left with no ground at all — which is what left **Maharath and Sanjan** with nothing.
Maharath's shape is drawn 0.03 units off Sanjan's for the first 3.77 units inland from the
coast they share, and the wedge between the two opens onto the sea.

A run of shoreline is a slit when it comes back to within 0.5 units of where it started
after at least a unit of travel, encloses sea rather than land, and measures no more than
0.15 on the same area-to-length ratio as the holes. The winding is what does most of the
work: of the 9352 runs on this map that come back on themselves at all, **8778 enclose
land** — headlands, spits and narrow capes, which have to be left exactly as they are —
and 574 enclose sea.

**The 0.15 here is a judgement, not a measurement, unlike the one in 2c.** Those 574 runs
have ratios from 0.0005 to 1.0, thickest between 0.07 and 0.12, and they thin out across
the threshold rather than stopping: 18 in the 0.02 below it, 6 in the 0.02 above, the
widest filled measuring 0.1474 against the 0.1522 of the narrowest left alone. There is no
empty valley to put a line in. What makes it safe is that **the answer does not depend on
it** — run at 0.12, 0.15 and 0.20 the number of gaps filled moves 460 → 505 → 508 and every
number that matters is identical: 47 arcs separating nothing, 0.301% of the land inside no
face, 8 countries without geometry. The gaps that cost a country its ground sit far below
any of those thresholds.

Measured: 505 gaps filled, 647.26 units of shoreline, which puts 50.5 units² of ground back
into the land — an average wedge is 1.28 units long and 0.08 wide, well under the drawing's
own resolution. Both ends of the threshold are logged every run, because a threshold cutting
through a population wants watching rather than trusting.

Only the shells are repaired. A gap enclosed in a lake is a hole of the lake's own, and the
sign that tells a slit from a spit is the other way round on a ring that bounds water. A
slit straddling a ring's own first vertex would be cut in part rather than whole, since the
closing vertex is never removed; nothing detects that case, so it is not known whether this
map has one.

**2d.** Coast rings need splitting at the points where Phase 3 attaches a tracer
endpoint. Easiest to defer: feed whole coast rings into Phase 3 and let the noder
do the splitting. Each ring is therefore added as one closed arc for now.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 5241 coast shells, 462
lakes kept, 2632 holes discarded as gaps and 505 filled as slits, giving 7140 arcs in total
with the tracers.
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

Two invariants. Both are now checked in the run; **one of them is failing.**

- **Every half-edge lies in exactly one ring.** This is what "the faces tile" means, and
  it is checked directly rather than inferred from areas. It holds, and it is the first
  thing to look at if a face ever comes out wrong.
- **The face areas account for the land.** This was written up as checked and holding, and
  it was neither: the run logged the face total and nothing compared it to anything. The
  comparison was made by hand, once, when the excess was 65,303 units of lake that a border
  crossed - which the clipping in 2c-bis has since removed.

  `ICoastlineService.AddArcs` now returns what the faces have to come to - the land, plus
  every hole discarded as a gap since nothing bounds those any more, plus every lake since
  a lake is a face of its own as well as a hole in the land around it - and `BuildFaces`
  checks itself against it.

  **It fails by 22,376.5 square units, 0.382% of the land**, and the comment in the code
  said all along what that means: a face has failed to close. That land is inside no face
  at all, so no country can be given it, and it is why Maharath, Sanjan, Siam, Dumarune and
  Sulka come out of the pipeline with no territory. Maharath measures 6,942.5 of its 6,946.9
  units uncovered and Sanjan 632.7 of 632.9.

  `MaximumAreaShortfall` is 0.1%, which is not zero because the signed area of a shell that
  crosses itself understates the ground it covers and four of those exist. The figure to
  watch once the closing failure is fixed is whatever those four leave behind.

**4d. Report the arcs that separate nothing.** An arc with both of its sides in one ring
divides no two regions from each other: the ring walks out along it and straight back, so it
lies on no cycle. For the coast that is ordinary, since an inlet one arc wide has sea on both
sides, but a border that separates nothing means the chain it belongs to never closes, and
whatever it was drawn to enclose is left open to what lies outside. **47 of them on this
map**, down from 61 once 2c-ter and 4f were repairing the two causes found so far, and the
twenty longest are named with their coordinates so that `--debug-svg` can find them by
`data-arc-id`.

That list turns out to be the inventory behind three reports that had looked like three
separate problems:

| symptom | what the chain did | where it shows |
| --- | --- | --- |
| two countries share a face | failed partway, so the region is not divided | 5b, 14 fused faces |
| a country gets no ground at all | failed completely, so the region never closes | 4's area check, 0.301% |
| an arc end resolves to nothing | fell short of what it should meet | 3d, 1 end at 12.48 units |

Every one of the fused faces has a bridge beside it - Deseret and Arizona on arc 8377,
Gwennland on 11499, Kovenda on 9496, Bactria and Sogdia on 8565, Vantong on 790, Tuscany on
948, Kalimantan on 11663, 11625 and 11637 - and arc 9496 sits at 3704.35, 2970.64, which is
the unresolved end from 3d.

**This list is the inventory of a symptom, not of a cause, and reading it as a cause was
wrong.** The entry recorded here before — border arc 11733, 144 points from 4424.6, 2025.1 to
4412.9, 2057.2, "Maharath's own boundary", with the repair being "a line in the source map" —
had it backwards. Arc 11733 is the body of a border that is drawn perfectly well. What was
wrong was the *coastline* beside it: a gap between Maharath's shape and Sanjan's, reaching
the sea, which the union kept as a zero-width slit and the hole test in 2c could never see.
The border had no choice but to separate nothing, because it was running up a slit whose two
walls the traversal could not tell it from. 2c-ter fills those, 4f drops the border copies
left over, and between them the count here falls from 61 to 47 and seven countries get their
ground back.

So an arc separating nothing is worth reporting, and it is still how these defects announce
themselves — but each one is a question rather than an answer, and at least some of the
remaining 47 will have a cause the pipeline can fix too.

**4e. Zero-length arcs are a second, smaller defect of the same family, and were at first
mistaken for the one above.** Welding and splitting leave two *distinct* nodes at one identical
coordinate - 44 coordinates carry more than one node - and the 50 arcs joining such a pair
have no length and so no direction. `GetDepartureAngle` falls back to a coordinate equal to
the start when it can find no different one, so `Atan2(0, 0)` makes such an arc claim to
leave at angle 0 whatever its real direction, which is enough on its own to send the
traversal the wrong way at that node. Merging the nodes and dropping the 58 arcs left with
no length recovers Tangeria and takes the land lost to self-crossing rings from 18,816
square units to 5,699 - and leaves all five missing countries missing, which is how it was
ruled out as the cause here. It also moves the area shortfall from 0.382% to 0.670%, because
the ground Avanarsuaq used to lose at assembly, in a face dropped for crossing itself, is now
lost one step earlier in a region that never closes.

What it does do, measured on one build with the merge switched off and on, is take the arcs
that separate nothing from **84 down to 61**. So it is a real repair to the topology, just
not the one that was being looked for.

**4f. Drop a border that is the coast drawn a second time.** Two arcs joining the same pair
of nodes and running along each other the whole way are one line drawn twice. Where one is
coast and the other a border, that is exactly what it is: a border cannot divide land from
sea. Both copies then leave each of the two nodes they share, the four half-edges interleave
in the order 4b sorts them into, and a traversal arriving along the coast takes the border as
its next turn and steps off the shoreline onto it — from where it walks the border network of
whatever lies inland into the outline of the landmass. That is what left **Siam, Dumarune and
Sulka** with nothing.

Measured before 2c-ter was repairing anything: 84 pairs of nodes carried both a border and a
coast arc, 48 of those ran within 0.0413 of each other end to end, the next three were
0.1667, 0.2192 and 0.5595 apart, and the remaining 33 were alternative routes two units and
more away. **Any threshold between 0.0413 and 0.1667 picks out the same 48**, so the 0.1 used
is a measurement. With the slits filled first, 34 are left to drop — 4.11 units of line, the
furthest 0.0336 from the shore — the rest having gone with the slits they were lying along.

**Length is no part of this test and cannot be.** Dumarune's duplicate is 0.21 units long and
Sulka's 0.0002, far below anything that could be told apart from a border merely touching the
shore by how long it ran. What makes them provable is the pair of shared nodes: noding has
already found that the two lines meet at both ends. A geometric trim was tried first, cutting
any stretch of border that stays within 0.02 of the shore for at least half a unit — it finds
10 stretches and recovers Siam, but it cannot see Dumarune's 0.21 or Sulka's 0.0002, and run
*before* 2c-ter it cuts away the one border dividing Maharath from Sanjan and the two come
out as a single face.

A separate hazard found looking for this one: **109 nodes have two half-edges leaving at
genuinely equal angles**, where the order falls to `ThenBy(arc.Id)` and is therefore
arbitrary. One of them, node 633 at 2440.35, 857.64, sits inside the face dropped for
Avanarsuaq. Breaking those ties properly needs the arcs compared further along than their
first vertex.

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

**5b-bis. A country claims a face by how much of it the country covers, not by whether
its shapes contain one point of the face.** The point test was wrong in both directions
and the second one was invisible:

- It **reported almost no fusions**. A face shared by two countries only contains a point
  of both where their two shapes overlap *each other*, and on a fused pair that overlap is
  a sliver along some other border they really do share — not the face. So the test fired
  on the slivers and missed the fusions.
- It **gave the face away by accident.** Whichever country the single point happened to
  fall in took the whole face, even where another country covered most of it.

Each country's claim is now the area of the face its shapes cover, unioned across its own
shapes rather than added up, since those routinely overlap each other. The largest claim
owns the face; anything under `MinimumClaimShare` is not a claim at all.

**`MinimumClaimShare` = 0.15**, from the distribution of all 10,397 claims the 6586 land
faces attract:

| share of the face a country covers | claims |
| --- | --- |
| under 0.05 | 3776 |
| 0.05 to 0.10 | 4 |
| **0.10 to 0.20** | **0** |
| 0.20 to 0.90 | 40 |
| 0.90 and over | 6577 |

Sharply bimodal with an empty tenth in between: at 0.10 and at 0.20 the same 33 faces come
out with two claimants, so any threshold inside it reads the data the same way.

The claim is capped at the face's own area, because the fallback for a shape too broken to
overlay measures an envelope instead of an intersection and an envelope can be larger than
the face. 42 claims are measured that way; before the cap one of them read 1.94 of its
face.

**The real number of fused faces is 33, of which 14 are large enough to matter** — not the
17 and 2 the point test saw. All 14 are reported individually with both countries and both
shares. Nineteen of the 33 are partial fusions where the face genuinely straddles two
countries; the other 14 are faces two countries each cover entirely, which is four pairs
drawn with the *same* shape plus ten faces of all but no area where the share is 0/0 noise.

**There is no enclave population to handle.** A rule giving a face to the smaller of two
countries that both cover it — on the grounds that a container drawn solid covers its
enclaves — was considered and dropped: of the 14 faces that would qualify, four are the
duplicate-shape pairs and ten are degenerate. Nothing on this map is an enclave drawn
inside a solid container.

**What this cannot do is divide a fused face.** Without the tracer there is nothing to
divide it along, so the whole face goes to the larger claimant — and where a country's
entire territory *is* one fused face, that country comes out with nothing. The new rule
changes which of the two it is: Kovenda and Vantong gain everything, Bopedi and Prabang
lose everything, where before the point decided it. Deciding by area is at least
deterministic and explicable, but the only real repair is the missing tracer, which is now
named in the report. Cutting the face along the two shapes' own boundary would recover both
countries and would keep exact sharing, since both pieces would come from one cut line; it
is not done here because it puts geometry into the output that no traced face produced.

**5c. Unclaimed land faces are absorbed** into the neighbour they share the most border
with. This is the whole of gap filling in this representation.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 6584 faces assigned to
countries, 466 left as water, 46 of 60 unclaimed faces absorbed, and **14 genuine missing
tracers reported** where the point test reported two. Nine countries come back from nothing
— Damarana 381,874 square kilometres, Ute 288,088, Etuguay 172,043, Siksika Range 147,342,
Vantong 142,328, Florida 126,033, Kovenda 97,733, Savoy 26,194, Ekara 16,137 — taken back
from the neighbours whose shapes had been catching their faces by a point, Walpira
−381,874, Uruguay −172,043 and others. Two are lost, Bopedi and Prabang, both to the
fused-face rule above. National countries with no geometry at all: 20 before, 17 after.
Total national land moves by −0.000%, so this is redistribution and not creation.

The cost is time: claiming by area is 7110 faces against every shape whose envelope meets
them, which takes the assignment from about 3 seconds to about 25.

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

**6d. Simplification decides once per arc, not once per polygon.** Simplifying each polygon
on its own gives the two countries either side of a border different geometry, which undoes
the one thing the pipeline is built for. What the coarse outputs do instead:

1. Each arc is simplified on its own, as an open line with **both ends pinned**, since those
   ends are nodes where it meets the arcs around it.
2. What that produces is not a new line but **the set of coordinates to drop**, collected
   across every arc.
3. Every ring in the output is then filtered through that one set.

Dropping by coordinate rather than by position is what carries the decision to both sides: a
ring is a run of arcs, each side of a border holds the same coordinates, so each side loses
the same ones. `VisvalingamSimplificationService` is now an open-line simplifier only; the
ring-based polygon method it used to expose is gone, since nothing can use it without
breaking the guarantee.

Three details that are not incidental:

- **Node coordinates are subtracted from the set afterwards.** An arc never offers up its
  own ends, but another arc may carry the same coordinate somewhere in its interior, and
  dropping it there would take a junction with it.
- **An arc that closes on itself keeps at least 6 points** (`ClosedArcMinimumPoints`). An
  island is one closed arc, and simplifying one down to a triangle loses it; 6 is the floor
  the old ring-based simplifier used, kept so the coarse outputs lose no more small islands
  than before.
- **A ring left with too little to close is omitted, not kept unsimplified.** Keeping it
  would be the one thing that breaks sharing, since its neighbour would have dropped those
  coordinates and it would not. It costs 8 rings of 6467 at 0.001 and 10 at 0.01.

**Verified**: 951,315 of the 1,172,853 arc coordinates go at 0.001 and 1,086,487 at 0.01,
leaving files of 5.63 MB and 2.36 MB against 5.59 and 2.35 before, so the tolerances still
mean what they did. Against the per-polygon output, segments shared exactly rise **4.4% at
0.001 and 25% at 0.01**, and segments with one side fall 2.3% and 11%. The improvement is
not larger because per-polygon Visvalingam is deterministic, so neighbours often made the
same decision anyway - what changes is that it is now guaranteed rather than coincidental.
The in-run check reports 0 segments shared the wrong way round and 0 used three times in the
simplified national layer.

Still to do:

- **4 faces covering 5,699 square units still trace to a ring that crosses itself** and are
  left out, owned by Avanarsuaq, Kalimantan, Thelemark and Epsumba. Down from 27,878 since
  4e, which left the count at four but took most of the area. Unlooping does not reach them,
  so they are not simple self-touches. **The cause is not duplicated tracers**: the census of
  Phase 1b was run again inside the window of each and found no overlap run of even 2 units,
  so the guess recorded here before — collinear overlap along the sea coast — was wrong.
  Kalimantan has three arcs that separate nothing beside it and Avanarsuaq one, so 4d is the
  first place to look. Epsumba's face is fifteen points at a single coordinate and
  Thelemark's unloops into eleven lobes, which suggests more than one defect among the four.
  They are written out on their own by `--debug-svg`, below.
- **6d is done: see below.**
- **6e, TopoJSON.** The sharing it needs is measured in Phase 7 item 4, with a residue
  attached: it holds for 260,643 segments of boundary and not for about 11,785.

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
4. **Exact sharing: run, and it passes.** For each pair of adjacent countries the shared
   boundary extracted from each is coordinate-identical. This is the acceptance test for
   the whole project, so it is worth being careful about what does and does not count as
   evidence.

   The test is a planar-partition count over the finished GeoJson, keyed on the
   coordinates as written to six decimals - which is what a consumer actually reads.
   Every segment inland should appear exactly twice, once from each side and in opposite
   directions, and the segments appearing once should be the outer boundary of the whole:
   sea coast and lake shore.

   Measured on `out-no-substates.json`, 1,341,790 segments over 1227 countries:

   | | |
   | --- | --- |
   | used by two different countries, opposite directions | 260,541 |
   | used by two different countries, same direction | 102 |
   | used twice by the same country | 5 |
   | used once | 820,494 |
   | used three times or more | **0** |

   **That once-only count needed a source outside the output, and comparing counts was the
   wrong way to use it.** The arc graph has 821,741 segments of `Coast` and `Lake` arc, the
   only boundary a single face has, against 820,494 segments used once in the output - 0.152%
   short, which looked like a clean pass and was reported as one.

   It was not. Asking the right question - is each once-only segment *actually* a coast
   segment - shows **11,785 of them are not**: 0.878% of the boundary is border with one
   side to it. The count check had netted two errors against each other, roughly 12,000
   unpaired borders against roughly 14,000 coast segments missing from the output, and read
   the difference as agreement. The check now tests membership instead, and the four dropped
   faces turn out to account for only 1,141 of the 11,785, measured by adding their
   boundaries to what is allowed one side.

   **Where the other 11,785 come from is open.** It is not simplification - the count falls
   to 6,155 and 4,889 in the coarse outputs, which take away paired boundary faster than
   unpaired. The likeliest cause is the union in 6a modifying an edge here and there rather
   than following it, which is the assumption this item set out to test; it holds for
   260,643 segments and not for about 11,785.

   **Two of the three checks turned out to be no evidence at all**, which a control showed
   and reasoning alone did not. Thinning every ring independently - keeping every other
   vertex, which is the failure per-polygon simplification causes - destroys 74% of the
   sharing, and yet:

   | | real output | sharing deliberately broken |
   | --- | --- | --- |
   | segments shared by two countries | 260,541 | 67,164 |
   | used three times or more | 0 | **0** |
   | once-only vertices of odd degree | 0 | **0** |
   | once-only count against the coast | **−0.152%** | **−34.057%** |

   The triple-use and odd-degree tests stay clean through a 74% failure, because each
   country ring is a closed loop whichever coordinates it is made of. Only the comparison
   against the independently counted coast separates the two cases, by a factor of 224 in
   the residual.

   Three findings came out of it, none of them a sharing failure:

   - **102 segments are shared in the same direction, all of them by one pair, Chukcha and
     Nauka.** Probing either side of each - 204 points - puts 102 in Chukcha only and 102
     in Nauka only and **none in both**, so the two do not overlap: the coordinates are
     identical and the interiors are on opposite sides. The cause is winding. Across the
     national output 6103 shells are counter-clockwise and **2 are clockwise**, and RFC
     7946 asks for counter-clockwise shells, so this is a conformance nit worth fixing for
     consumers that care, and nothing to do with sharing.
   - **The substate layer has 2 segments used three times**, by Capital Territory twice and
     Falkland once, with 2 vertices of odd degree beside them. A self-touching ring, and
     the one real defect the check found.
   - **5 segments are used twice by a single country** and 2 shells and 1 hole have zero
     area - degenerate spikes, two of them under a micro-degree long.

   So assembling a country by unioning its faces follows the edges those faces already share
   for 260,643 segments of boundary and not for about 11,785 of it - an assumption this plan
   rested on from Phase 6a onwards, now a measurement with a residue attached. `out-no-substates.json` and
   `out-only-substates.json` are exactly shared; `out.json` is too, within each layer, but
   carries 3766 segments used three or more times because it holds both layers at once and
   a national border is also a substate edge. `out-low.json` and `out-medium.json` are not
   shared and cannot be until 6d.

   **It now runs in the pipeline**, as `IVerificationService`, once for each layer that
   gets written, between unprojecting and writing: rounding to the output decimals happens
   as the file is written and can only bring two coordinates together, never separate them,
   so sharing there is sharing in the file. The two layers are checked separately because
   each is a partition in its own right and the two together are not - a border between two
   countries is also the edge of a substate, which is why `out.json` carries 3766 segments
   used three times and is not a defect.

   What it reports on this map:

   | | national | substates |
   | --- | --- | --- |
   | segments | 1,341,790 | 349,208 |
   | shared by two countries, both sides identical | 260,643 | 80,605 |
   | used once | 820,494 | 187,990 |
   | shared but wound the same way | 0 | 0 |
   | used twice by one country | 5 | 1 |
   | used three or more times | 0 | **2** |
   | coast and lake segments in the graph | 821,825 | not comparable |
   | one-sided and not coast, lake or a dropped face | **11,785** | |

   `MaximumUnpairedShare` is 10%, set to catch a break rather than the residue above: twice
   the worst figure a sound run produces, against the three quarters of all sharing that the
   thinned control loses. It is deliberately loose, and it should be tightened the moment the
   11,785 are understood.

   **Measured again after 2c-ter and 4f**, which recovered seven countries between them, the
   same check reads 265,436 shared and **6,892 unaccounted — 0.513% of the boundary, down
   from 0.887%**. The figures above are left as they were measured, since the reasoning that
   got to them stands; where this plan quotes 11,785 as the size of the residue, 6,892 is the
   current one. The two coarse outputs moved the same way, 2.425% to 2.024% and 4.954% to
   4.538%.

   **The in-run check and the check over the file disagree about one thing, and both are
   right.** Over the file, 102 segments - all of them between Chukcha and Nauka - come up
   shared in the *same* direction. In memory they are properly opposite. The difference is
   winding: rings are wound however the face they came from was traced, 821 of them against
   the convention, and the writer puts nearly all of that right on the way out. Two shells
   escape it and reach the file clockwise, and in the file those two look as though they
   meet their neighbour the wrong way round. So the in-run check orients every ring before
   comparing, because in-memory winding is arbitrary by design and counting it would be
   821 false alarms a run.

   **That leaves one thing the in-run check cannot see: the two shells the writer leaves
   clockwise.** Their coordinates are shared either way, so it is not a sharing failure,
   but RFC 7946 asks for counter-clockwise shells and a consumer may care. It is checkable
   only on the output, and it is open.
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

## What is left, in the order worth doing it

1. **Dissolve a country by walking its arcs** rather than by unioning its faces. Phase 7
   item 4 measures the union as following the edges it is given for the great majority of the
   boundary and not for a residue now standing at 6,892 segments, and skipping the union
   outright took that residue from 11,556 to 4,614 when it was measured - about 60% of it
   removed by construction rather than by measurement. It is also the alternative this plan
   named at the outset.
2. **Whatever that leaves.** When last measured 3,535 of the residue existed in no arc at
   all, so something between the faces and the output is inventing coordinates; the unlooping
   in 6a is the next suspect. Both figures want re-measuring after 1.
3. **The 47 arcs that separate nothing**, down from 61. Each is a question rather than an
   answer: two of them turned out to be the coast's fault rather than the border's, and 2c-ter
   and 4f fixed those in the pipeline, so reading the rest as "work on the source map" — as
   this plan did — is an assumption and not a finding. Arc 9496 at 3704.35, 2970.64 is still
   the one with a cause already known, and it is also the unresolved arc end from 3d.
4. **The 109 nodes where two half-edges leave at genuinely equal angles**, where the order
   falls to `ThenBy(arc.Id)` and is arbitrary. One of them, node 633 at 2440.35, 857.64, lies
   inside Avanarsuaq's dropped face. Breaking those ties needs the arcs compared further
   along than their first vertex.
5. **6e, TopoJSON**, which wants 1 and ideally 2 done first.
6. **Two shells reach the file wound clockwise** where the writer puts the rest right. Not a
   sharing failure, but RFC 7946 asks for counter-clockwise shells.
7. **Eight countries still have no geometry**, down from fifteen: Bopedi, Ligury, Manunan,
   Mauretania, Padun, Prabang, Sascatchewa and Winnebagee. Each is a region that never closed,
   and the two causes found so far - a gap in the coast open to the sea, and the coast drawn a
   second time as a border - are both now repaired, so these eight have a third cause waiting
   to be found. The method that found the first two is in this plan's history: take one
   country, dump every arc, node, departure angle and ring in a window around it, and follow
   the traversal step by step until it turns the wrong way.

## Order of work (as planned at the start)

0d and 0e first — they are standalone bug fixes, independently verifiable, and
everything downstream depends on the tracer layer being read completely. Then
0a–0c, then Phases 1–2 (verifiable by writing the coast out as GeoJSON and looking
at it), then 3–4 (verified by face count and dangle count), then 5–6. Phase 6e
(TopoJSON) and 6d (arc simplification) can land after the GeoJSON output is passing
Phase 7.
