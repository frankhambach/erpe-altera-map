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

A second export, `Altera_Data_Generating_Map_20261014.svg`, is now in use. Where the first
export's third to fifth decimals took only a handful of values, as though the coordinates
had been put on a sub-unit lattice before writing, the second carries the drawing as it was
drawn. That finer geometry is what brought out **2c-quater**, **2c-quinquies** and **3b-bis**
below, and those three and **1-bis** carry their own figures and say which export they come
from. Everything else here is still the first export's and has not been re-measured against
the second.

The second export has itself been corrected once in place, on 15 October, where a tracer
that was missing had left Iveria and Ossetia sharing a face. The three passages' figures are
from the file as it stands after that correction; they moved when it arrived — arcs that
separate nothing fell from 4 to 1, two countries got their geometry back and the count went
from 1223 to 1225 — which is the reason each of them says what it was measured against
rather than only what it measured.

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
   `SvgPath`, `SvgLine`, `SvgPolygon` or `SvgRectangle` **and that the author has not
   hidden**. See 1-bis.
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

**1-bis. An element the author has hidden is not part of the map.** `display="none"` is
how a drawing says "not this", and reading it anyway does not merely add linework — it
makes work that has to be undone further down. The two hidden paths in `Border_Tracers`
on the second export are one line drawn twice, a dark stroke and a light one over it,
both starting at 3833.732666, 1301.5913086, and they were the only two paths at that
coordinate. 1b was spending its duplicate rule collapsing the second onto the first to
arrive back where the drawing already was.

Leaving them out, measured against the same run that read them:

| | before | after |
| --- | --- | --- |
| duplicated tracer stretches collapsed in 1b | 15, 1036.8 units | 14, 903.3 units |
| arcs that separate nothing | 1 | **0, and the report no longer fires** |
| faces traced | 7006 | 7000 |
| crossings to node in 4a | 136 | 131 |
| arcs in the topology | 9985 | 9979 |

The 133.5 units the collapse loses are exactly the repeat it used to report, arc 964 on
arc 372 at that coordinate, so those two rows are one finding seen twice. The arc that
separated nothing was the **last one left** — 51 points from 3910.41, 1294.84, a chain
that never closed, 77 units from the pair — and it went with them. Six faces fewer is the
two copies no longer dividing ground the tracers around them already divide: the land
inside a face holds at 5,854,540.0 of 5,855,297.0, the countries at 1225, the segments
used twice by one country at 0, the unaccounted boundary at 3 segments, and no face is
claimed by two countries by enough to matter.

**The rule belongs to the whole document, not to this phase.** The rim and the country
shapes are read by two filters of their own in `ConvertCommand`, and all three had the
same type test written out separately — which is exactly how only one of them came to ask
about `display`: the rule lived in three copies, so it was changed in one. The four
element types and the visibility test are now one extension method all three call. Every
output file is byte-identical across that change, because nothing in the rim or the shapes
is hidden on this export; it is the three copies that were the defect, not what they
currently decide.

Known limitation: the rule asks the element and not its ancestors, so linework inside a
hidden **group** is still read — and hiding a whole layer is what Illustrator does. Both
hidden elements on this map are paths, so the shape of that case is a guess rather than a
measurement, and guessing at it is how the three copies got out of step in the first
place.

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

### 1b-bis. A tracer that repeats a stretch of *itself*

**Some borders double back along themselves.** The arc leaves a coordinate, runs out, and comes
back to that *exact* coordinate before carrying on. A stretch like that has drawn a line and not
a boundary: nothing can be on one side of it, because both of its sides are the same side. It is
1b's defect with one arc in place of two, so it is cut out in the same step, and **first**, since
what makes it expensive is what 4a does with it.

**26 such loops on this map over 19 arcs.** 16 are a vertex written twice where the drawing
stopped and started again. The other 10 are excursions of 0.64 to 1.66 units — and they are not
thin lenses but *straight lines*. Every vertex of the excursion on arc 1181 lies on one line to
within 9e-07, so the return leg is the outbound leg with its vertices spaced differently. That is
why it is invisible in `--debug-svg`, and why comparing each point with the one before it does not
find it.

**The damage comes through 4a.** The two legs lie on top of each other, so noding cuts the arc at
every vertex of the overlap. 44 units of border between **Kalimantan and Daha** came out as
thirteen pieces, ten of them under 0.3 units. The traversal walks down the border, into a needle
and back out, on to the next needle and back out, and then retraces the whole 27 units it has
already walked in the other direction — one ring of 33 half-edges with *both sides of the border
in it*, so the two countries came out as one face, with three faces of 0.000003 square units and
less left beside it.

**A needle does not have to come back to a vertex.** The test above looks for a coordinate the
arc visits twice, and a cusp left by a badly drawn bezier often does not oblige: it runs out,
reverses, and comes back to a point part way along the leg it went out on, *between* two of that
leg's vertices rather than on one of them. Arc 412 runs 1.85 units out from 5428.35, 2824.64,
turns through exactly 180 degrees at 5426.15, 2825.74, and comes 0.17 units back to 5426.35,
2825.64 — on the outbound leg, and no vertex of it. Nothing repeats, so nothing is found.

So **a reversal is looked for as well**, and the whole straight run around it is replaced by the
segment from the point where the arc entered the line to the point where it left. That loses
nothing: every vertex of the run is on one line, and what the run draws is that segment walked
forwards, backwards and forwards again.

The population is as clean as the one above. Of the **169 vertices where a tracer turns by more
than 150°, 14 turn by 180.0000** and the next turns by **179.8903**; all 14 have a straight run
either side deviating from the line through its ends by **0.0000000** units, and the other 155
have no straight run at all — they are **sharp corners**, which are drawn boundary and stay.

Two details of the measurement earn their place. The reversal is read as a **sine** rather than an
angle, because arc cosine loses its precision at exactly the place this asks about. And the rule
rests on a **deviation guard of one grid step**, measured against the line joining the two ends,
rather than on the 1° used to walk outwards from the cusp: an angle says nothing about how far the
far end of a long run has drifted.

**Ten of the 14 were already going**, because in those arcs the needle does return to a vertex.
The four that were not are the three arcs 4d reported as separating nothing that nothing else
could account for — 11204 on arc 232, 11271 and 11273 on arc 412 — and **both of the faces still
being rejected for crossing themselves**, both on arc 1337.

| | before | after |
| --- | --- | --- |
| arcs separating nothing (4d) | 5 | **2** |
| faces rejected for crossing themselves | 2 | **0** |
| needles cut | 25 over 19 arcs | 28 over 22 |
| crossings noded (4a) | 173 | 157 |
| unaccounted boundary (7) | 5,595 segments | 5,567 |

Gimbiyu gives 65 square kilometres to Wagima and five other countries move by under 2; the net
over 1,590 rows is −1.7, so this is redistribution. The land inside no face does not move.

**Told apart by what the loop encloses**, as an area over its length — the hydraulic radius, for
the fifth time in this pipeline. **`MaximumNeedleAreaToLengthRatio` = 1e-6**, and this one needed
no judgement at all: 22 of the 26 loops enclose *exactly* nothing, three come to 4.0e-10, 5.6e-10
and 7.7e-10 which is the residue of summing a shoelace along a collinear run rather than an area,
and one comes to 0.00758 and is a real loop — 0.19 units of border drawn as a closed speck, which
stays. **A gap of nine million**, and the figure sits at the middle of it on a log scale, 1300×
above the largest residue and 7600× below the one real loop.

The arc keeps both of its nodes, because the coordinate the needle returns to is the one it left.
An arc that is nothing *but* a needle would go entirely; none is.

**Measured: 25 needles out of 19 arcs, 9.61 units of line, the longest 1.66.** And the striking
result is not the fused face:

| | before | after |
| --- | --- | --- |
| **arcs separating nothing (4d)** | 24 | **5** |
| faces two countries claim at once (5b) | 3 | 2 |
| faces rejected for crossing themselves | 4 | 2 |
| crossings noded (4a) | 213 | 173 |
| open arcs for 4e to contract | 55 | 28 |
| unaccounted boundary (7) | 0.417% | 0.412% |

**19 of the 24 arcs that separated nothing were needle debris.** A list this plan had been reading
as 24 separate questions was mostly one defect. The arcs 4e has to contract halve for the same
reason: many were noding fragments of needles.

Four countries move, and all four land within 0.6% of expected — Daha 2,789 → **95,971** against
95,438, having been getting 2.9% of its territory; Kalimantan 195,536 → **102,353** against
101,878; and a second needle nobody had reported takes Kham 271,776 → **271,850** against 271,352
and Golok 13,869 → **13,795** against 13,759. Six others wobble by about a square kilometre from
the line removed and the net over 1592 rows is +0.6, so this is redistribution. **The land inside
no face does not move**, so needles were costing division and not ground.

Two measured thresholds for the duplicates between *two* arcs:

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

**2b.** `land = UnaryUnionNG(the unitary and federated states' polygons)`.

**Not every country's polygons, because a federal state's ground is drawn twice over**:
once as its own shape in `Country_Shapes` and once as the federated states that make it up
in `Substate_Shapes`. Unioning both versions means the coast along such a state can come
from either drawing, and where the two disagree it comes from whichever was drawn further
out — the shape and its own subdivisions would have to be reconciled before the sea could be
found. The federated states are the finer drawing, and the ones Phase 6 assembles the
substate layer from, so they are what the coast follows. Unitary states have no
subdivisions and are unaffected, which is why the test is on the country's type rather than
on the layer an element was read from.

The assumption is that the two layers agree about where the land is: should a federal state
ever carry ground that none of its federated states cover, that ground is now outside the
coastline rather than inside it.

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

  **The valley's upper bound was itself a gap.** The 0.1965 quoted above as where the lakes
  begin is one single hole, 2.49 square units between Padun's shape and Kunmin's, and it is a
  gap. With it and one more removed the lakes begin at 0.2551 instead. The ratio was not
  wrong about the population; it was wrong about this member of it, and no threshold fixes
  that, because the gap is genuinely as fat as a small lake.

  **So a hole is also a gap when a border runs through it rather than merely into it**, judged
  by the length of tracer inside it over its hydraulic radius. That measure is deliberately
  elongation-aware: a border following the length of a sliver scores far higher than one
  crossing a pool. Measured over the 462 holes the ratio keeps, two come to 18.88 and 8.19 and
  the rest top out at 1.88 — a span of 4.4× with nothing in it. The two are the Padun and
  Kunmin gap, with 3.71 units of border along its 2.49 square units, and a second of 0.98
  square units with 1.92 along it, which turns out to be the gap between Deseret and Ute.

  The threshold sits at 6 rather than in the middle of that span, because a compact lake
  crossed straight through would score about 4 and has to survive. Nothing on this map is
  near it — the highest genuine lake is 1.88 — but that is the measure's weak point and not
  something the data here can rule out.

  Either test is enough on its own, and each catches what the other cannot: the ratio catches
  the 868 gaps with no tracer anywhere near them, the crossing catches a gap wide enough to
  look like water.

  **Three other criteria were measured and rejected:**

  | idea | why not |
  | --- | --- |
  | raise the ratio threshold | the two gaps sit at 0.1965 and 0.2347 and the lowest real lake at 0.2551, so catching them needs 0.235 — a tuned figure 1.09× under the first lake, not a mid-valley one |
  | a border passes through the hole, by length | real lakes carry up to 3.13 units of tracer against the gap's 3.71. No separation at all, which is the objection to the idea in the first place |
  | two or more country shapes bound the hole | 167 of the 462 lakes are bounded by two shapes or more, since a lake on a border is bounded by both |

  Measured end to end: 2 holes reclassified, lakes 462 → 460, lake area down by exactly their
  3.47 square units. 2c-bis now trims 374 tracers instead of 376 and removes 30.22 units of
  line instead of 39.23, the 9 units being the border that used to be cut away against these
  two "lakes". Countries with no geometry fall from 2 to **1**, the faces two countries claim
  at once from 6 to **4**, the arcs that separate nothing from 30 to **26**. Padun comes back
  with 98,903 against 98,444 expected, Kunmin falls to 136,654 against 136,268, Ute comes back
  with 76,949 against 77,241 and Deseret falls to 208,984 against 209,017.

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

**2c-quater. Cut a tracer off the shore where it was drawn along it.** 2c-bis forbids a
tracer the whole inside of a lake. The sea cannot be forbidden the same way, because its
edge is a line a border legitimately *ends* at — and 2c-ter does not catch every slit, so
some of those edges are hairline needles with a border drawn down them. Where that happens
the shore and the border carry the same ground twice, both bound the faces traced there,
and the result is a segment three rings use at once: on the second export **Khumer and Siam
over 1.4 units of one and Namiba over 0.5 of another**.

The two slits behind those miss 2c-ter for different reasons, which is worth knowing before
anyone widens it: one comes back after 0.64 units, under the 1 unit of travel the fill asks
for, and the other never comes back within 0.74 units of its mouth, over the 0.5 it allows.
Neither is a near miss in the ratio — at 4e-05 and 7e-05 they are hairlines — and the
second misses the width by 10%, so a wider fill would take it. The fill's width is also the
most it may move the shoreline, so the border is the thing to cut rather than the coast.

So: difference each tracer against the shore **as the coast arcs carry it**, grown by
`ClipMargin`. After the fill, not before — clipping against the shells themselves would
cut the borders that run up a *filled* slit, and those are the real borders 2c-ter exists
to give back. Then drop whatever the cut leaves seaward of the coast, since it bounds
nothing: one piece does on this map, 0.4766 units past the tip of the needle it was drawn
along.

**The minimum run is what makes this safe, and it is not optional.** A border reaching the
coast crosses the margin at some angle and loses a sliver of its last segment to it, the
shallower the approach the longer the sliver. Measured over the 321 tracers that lose
anything at all to the margin: 304 lose under 0.01 units, a tail of 8 reaches 0.093 — an
approach of half a degree — and then the nine runs that follow the shore start again at
0.159 and go to 1.415. `MinimumShoreRun` sits in that gap. It is 1.7x and not an empty
decade, and what makes it safe is the geometry either side rather than the width of it:
below is one segment's worth of crossing the shore, above is a stretch drawn along it.

Cutting with no minimum at all was tried and is not merely pointless but harmful: it cuts all
321, and the welding that then re-attaches those pulled-back ends leaves **Lebanon and
Osurfine with no geometry**, fuses **Abeline and Osurfine** into one face of 5,300 units², and
takes the arcs that separate nothing from 1 to 4. That was re-measured against this pipeline
rather than the one it was written for, because 3b-bis has since removed one of the reasons it
hurt and the question was whether the minimum is still load-bearing. It is.

It also found a crash of its own, in the line that reports where a run is: at a minimum of
1e-09 the overlay rounds a 0.0011 unit run away to an empty intersection, and reading the
first coordinate of that threw. Unreachable at 0.1 — the run is by definition what the
difference removed — but the report is not the thing that should fail if the figure is ever
lowered, so it no longer does.

Measured on the second export, against the same run without this step:

| | before | after |
| --- | --- | --- |
| segments used by three or more rings | 2, and 1 at 0.001 | **0 at every tolerance** |
| segments used twice by one country, substate layer | 1 | **0** |
| arcs dropped as duplicates in 4c | 78 | 72 |
| faces traced | 7007 | 7006 |
| land inside a face | 5,854,539.8 | 5,854,540.0 of 5,855,297.0 |
| the run | 98 s | 100 s |

9 tracers are cut, 4.04 units of line in all, and one piece of 0.4766 units is dropped past
the tip of the needle it was drawn along. 25 of the 1225 countries move and the largest moves
by 0.0036%, which is the border going to where it leaves the water.

**The needles themselves stay**, and that is 2c-quinquies' work rather than this one's: this
cuts the border that was drawn along a needle, not the needle. Dematia's, the last segment
the sharing check reported as used twice by one country, has no border along it at all.

**2c-quinquies. Cut the needles out of the rings themselves.** A ring that leaves a vertex,
goes somewhere and comes back to within a hair of it without enclosing anything on the way
has drawn one line twice rather than gone round something. Nothing is on the far side of such
a run, so whatever is assembled from the ring walks its own line out and back — which is what
the sharing check reports as **a segment used twice by one country**, and on the second export
that was Dematia's 0.4685 units of **lake shore** at 3526.82, 2176.64, out and straight back
to within 0.0002 units of where it set out. Two shapes disagreeing about the water's edge by
a millimetre on the ground.

**A lake shore is where these are, and that is not luck.** It is the same defect 2c-ter fills
on a shell, arriving where 2c-ter cannot see it: the fill only ever repairs shells, so it has
already taken the thin slits off those and what survives on a shell is the fat ones it
refuses — while nothing has ever been taken off a hole. All five needles the scan finds in
the written arcs are lake shores.

The run is replaced by the vertex it set out from, which is what 2c-ter does with a slit, and
no winding test is needed to do it: a run that encloses nothing encloses nothing on either
side, so unlike a slit it does not matter which side the water is on. That is what lets one
rule serve the shells and the holes alike.

**The return tolerance is what replaces 2c-ter's minimum length.** The fill asks for a unit of
travel before it will take a run, because under that a ring's own jitter qualifies — 51,541
runs of two drawn segments folding onto each other — and Dematia's needle travels 0.937, so a
length minimum would miss it. What makes one unnecessary here is that the run has to come back
to within `MaximumNeedleReturn` rather than within half a unit: jitter does not return to
where it set out from, it wanders on. At 2c-ter's half unit it would, and 267 runs that
enclose exactly nothing over 0.04 units would go with it — which would be removing the
drawing. The five needles return to within 0.0002, 0.00036, 0.00103, 0.00106 and 0.00275
units, so ten times the widest of those is the tolerance.

What a run may enclose is then the discriminator, and it is measured: over the 2466 runs that
come back within the tolerance, five enclose between **9.2e-06 and 1.005e-04**, the next
encloses **8.733e-04**, and the rest run up to 0.95. `MaximumNeedleRatio` sits at the middle
of that gap on a log scale, 3x either side.

Measured on the second export, against the same run without this step:

| | before | after |
| --- | --- | --- |
| segments used twice by one country | 1 | **0 at every tolerance**, the first time |
| the run | 98 s | 100 s |

6 needles are cut, 2.1967 units of line, the longest running 0.937 units and the widest
enclosing 1.3e-04. Six rather than the five the written arcs show, because welding splits the
rings afterwards and a needle across a split is no longer one run. 9 of the 1225 countries
move, each losing a vertex or two, the largest by 0.0002%, and the total land moves by
+0.000037 of 21,560.78 square degrees — which is what a needle encloses: nothing.

The search looks 5 units ahead rather than 2c-ter's 50, because a needle is one line drawn
twice and the longest here runs 0.937 units. That is worth 4 seconds of the run, and **the
longest needle cut is reported every run for exactly this reason**: a figure creeping up
towards 5 is the sign that the search is being cut short. Like 2c-ter, this does not wrap, so
a needle straddling a ring's own first vertex would be cut in part rather than whole.

**2d.** Coast rings need splitting at the points where Phase 3 attaches a tracer
endpoint. Easiest to defer: feed whole coast rings into Phase 3 and let the noder
do the splitting. Each ring is therefore added as one closed arc for now.

**Verified** against `Altera_Data_Generating_Map(13).svg`: 5241 coast shells, 462
lakes kept, 2634 holes discarded as gaps - two of them for the border running through them -
and 505 filled as slits, giving 7138 arcs in total with the tracers.
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

**3a-bis. Drop the arcs 3a has collapsed, before 3b runs.** A tracer whose own two ends 3a
welded to each other has joined nothing: it has collapsed to a point. **68 of them on this
map**, 65 `Border` and 3 `Substate`.

Left in place such a stub does real damage, through what else was in the group its two ends
merged as. A neighbouring border reaching for the same place joins that group too and its end
comes to rest on the stub's node. That node is now referenced three times — twice by the stub —
so it is not a loose end, **3b never looks at it**, and the border is never welded to the arc
it was drawn to meet. The border is left hanging off a node with nothing else on it: an arc
that divides nothing, so the two countries either side of it come out as one face.

That is **Ligury and Tuscany**. The border between them, 10.74 units of it, ends on the node of
a stub 0.3 units long instead of on the border it was drawn to meet 0.88 units further on. In
`--debug-svg` the stub shows as a single point apparently unconnected to anything, sitting
exactly where the border ends.

**Collapsed is measured by what the arc encloses, not by where its vertices are.** The test
here used to ask whether every coordinate of the arc was exactly its node's, and that is only
the stubs 3a reduced to a single point — it misses the ones that kept a vertex of their own.

That is **Prabang and Vantong**, and the same defect read a second time. Arc 789 is 0.12 units
of scratch in the source map, its two ends 0.1 units apart, and arc 790's end — 44.13 units of
the border between the two countries — is drawn *exactly* on the first of them. 3a put all
three in one group, so node 1577 came out referenced three times and nothing split arc 9324
where it passes **0.058 units away**. The border dead-ended, one face covered both countries,
and Prabang got no ground. After the merge arc 789 runs out 0.034 units and straight back,
which is a point for every purpose that matters and no coordinate of which equals its node.

So the measure is the **hydraulic radius of the loop the closed arc makes**, area over
perimeter, which for a long thin region is half its width. A spike doubling back encloses
nothing and scores 0; a ring encloses something and scores on the order of its own size. The
threshold is **half the merge tolerance**, which says the enclosed region is narrower than the
distance 3a is allowed to close — so 3a could have made it, and there is nothing in it to keep.

That figure is derived rather than fitted, and the measurement agrees with it. Of the 70 closed
tracer arcs, 68 run from 0 up to **0.0578** and the other two are **1.1139** and **3.2849** — a
span of 19× with nothing in it. 0.25 lands within 2% of the middle of that span on a log scale,
4.3× above the widest spike and 4.5× below the narrowest ring. The two above it are real rings,
25.6 and 82.7 units round, drawn as loops whose ends 3a closed, and they stay.

**Tracers only, and that restriction is load-bearing rather than tidy.** The coast carries 5241
closed arcs and **1326 of them score under this threshold** — tiny islands, the smallest
reaching 0.045 units from its node — and their size says nothing about whether they belong.
They are never loose ends, since they come out of 2c already closed, so 3a never closes one,
and a measure of what 3a did has no business reading them. The 460 lakes would all survive the
measure anyway, the narrowest reaching 1.97 units, but they are left out on the same grounds.

Dropping the stub here, after 3a and before 3b, cannot disconnect anything: its two ends are
one node already, so everything else meeting there still meets there and only the stub goes.
What it does do is make the end it was holding a loose end again, which 3b then welds onto real
geometry.

Measured when the rule went in: countries with no geometry fall from 6 to **3**, the faces two
countries claim at once from 11 to **7**, the arcs that separate nothing from 41 to **31**. Four
fused pairs come apart into their proper halves — Athabasca and Sascatchewa, Suhinnan and
Manunan, Tuscany and Ligury, Duran and Tsuren — and Gwennland recovers the 107,530 square units
it lost to Thelemark when 2c-ter first went in, so that both now match their expected areas.

Measured again when the test was widened to the enclosed radius, where the striking thing is
how little else moves: **3b splits 2670 arcs instead of 2669**. The other 39 stubs newly
dropped sit on nodes of degree four, where two real arcs already met and nothing was hidden, so
they free no end at all and the change is hygiene for all but one of them. Countries with no
geometry fall from 1 to **0**, the faces two countries claim at once from 4 to **3**, the arcs
that separate nothing from 26 to **25**, the unaccounted boundary of 7 from 0.431% to
**0.420%**. Prabang comes back with 48,231 square kilometres and Vantong falls to 94,095 — a
ratio of 1.95 against the 0.66 to 0.34 share 5b gave the two shapes of the fused face, so the
traced partition reproduces the shapes' own division of it. The land inside no face does not
move.

The opposite case has to keep working and is tested: an arc whose two ends are brought together
but which encloses something wider than the tolerance has been *closed into a ring*, not
collapsed, and stays. So does a coast ring smaller than the tolerance, which the kind
restriction above is there to protect.

**3b. Endpoint ↔ segment interior — the dominant case.** For each endpoint still
unattached, find the nearest arc segment within `SplitTolerance`. Split that segment at the
foot of the perpendicular, insert a node there, and merge the endpoint into it. 2669 of them
in this file. **Do not treat this as the exception path** — it is how most of this map's
borders meet, by seven to one.

**An endpoint may weld onto its own arc, but not onto the stretch it has just come from**, and
"just come from" is measured along the arc rather than across the gap, by the same
`SplitTolerance`. Both halves of that matter:

- Excluding the whole arc, as this did, means **a tracer that loops round and comes back to
  where it began can never be closed**. The border that surrounds Bopedi is one: it starts at
  the coast, goes right round the country, and ends 0.18 units from an earlier part of itself
  with nothing else inside 12.48. So it was reported as an unresolved end, the loop never
  closed, the border divided nothing, and Kovenda and Bopedi came out as one face. Splitting
  the arc at the meeting point turns the loop into a closed arc and leaves the run out to the
  coast as a piece of its own, which is the topology that was drawn.
- Excluding nothing would be no better: an endpoint would find the segment beside itself at no
  distance at all and split its own arc next to its own end, which is how the stubs of 3a-bis
  are made.

Measured over all 2669 endpoints that reach 3b, allowing this changes **exactly one** of them,
and that one is Bopedi's. Countries with no geometry fall from 3 to **2**, the faces two
countries claim at once from 7 to **6**, the arcs that separate nothing from 31 to **30**;
Bopedi comes back with 43,551 against 43,387 expected and Kovenda falls to 54,179 against
54,005. Nothing else on the map moves.

**3b-bis. A split that lands on a vertex belongs to that vertex.** Splitting an arc puts the
new node in place of the vertex at the far end of the segment it splits — the piece before
the split carries the vertices up to that segment and then lets the node stand for the next
one. That is deliberate, and it is how a weld pulls an arc over onto the end it is taking.
It also means **the far vertex is discarded**, so which segment the split is expressed
against decides which vertex goes.

A split point that lands on a vertex can be written either way: as the far end of the
segment before it, or the near end of the segment after it. The two are the same point, but
only the first stands for the vertex it is actually on. The second stands for the *next*
vertex along and throws that one away, moving the border by up to a whole drawn segment for
a weld that should have moved nothing at all.

That is not a corner case. On the second export, of the 374 ends that weld onto an arc at no
distance at all, **334 land on a vertex the arc already has**, and 167 of those were written
the second way: the vertex each one discarded sat a median 0.60 units off, 91 of them more
than half a unit, the worst **4.14**. Counting the ends whose nearest point on an arc is a
vertex rather than a point inside a segment — the same mistake with the end off to one side
— it is **746 of the 2672 splits**, which the weld now reports every run.

**Which 167 it was came out of the order the segment index handed its candidates back in**,
because the search kept the last of several at the same distance. That order changes
whenever an arc is added or removed anywhere, so replacing nine arcs on one coast for
2c-quater moved borders hundreds of units away from them — Gurkha and Rumud at 4510, 1848,
390 units from the nearest cut, and two more pairs of landlocked countries. It took an arc
dump either side of the change to see it: the first reading was that the clip had moved
ground, which it had not. The tie is now broken on the arc's identifier, then the segment,
then the parameter, for the same reason 4c keeps the lowest identifier of a duplicate pair —
the same graph has to reduce the same way whatever else has changed.

Measured on the second export, against the same run without this fix:

| | before | after |
| --- | --- | --- |
| sub-grid stubs the splitting leaves, taken in 4e | 16 open, 17 closed | **3 and 3** |
| crossings to node in 4a | 143 | 136 |
| land inside a face | 5,854,534.5 | **5,854,540.0** of 5,855,297.0 |
| segments shared exactly from both sides | 270,853 | **271,023** |
| unaccounted boundary | 3 segments | unchanged |

It moves the output, which is the point: 520 of the 1225 countries change, 457 of them by
under 0.3% and the largest, Karasa, by 1.3605% of its 0.46 square degrees, while the total
land moves by +0.0119 of 21,560.77 square degrees. That is ground going back where it
belongs rather than appearing, since every restored vertex is a kink one neighbour gains and
the other gives up. It does **not** fix the segments three rings use — those are 2c-quater's.

Two rows this table used to carry have gone, and both say something: it took the segments used
twice by one country from 2 to 1, and it stopped 4g having to drop an arc that retraced
itself. 2c-quinquies now takes the needles behind both of those before the weld ever sees
them, so against the pipeline as it now stands this fix changes neither — the figures above
are measured against the current code with only this fix taken out, not against the pipeline
as it was when the fix was written.

A split on an arc's own first vertex has no segment before it and comes out with a segment
index of −1, which the piece builder reads as an empty piece between the arc's start node
and the new one: the geometry is kept whole and the zero-length piece is contracted away in
4e. Nothing on this map does it, but only just — the earliest of the 167 lands on segment 1,
one segment short of it — so the arithmetic has to hold rather than merely not be reached.

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
the tolerance. **Expect none.** The one entry this list used to carry, the end whose nearest
other arc was 12.48 units away, was not a drawing error at all: what it should have met was
its own arc, 0.18 units off, which 3b was excluding. This list is the manual-review queue for
each new export, and it must stay short — if it grows, something changed upstream. Each entry
is worth a country, so none of them is cheap.

It is also worth reading the other way round. An unresolved end says *nothing within the
tolerance*, and the natural reading is that the map is missing a line. Here the line was there
and the search would not look at it, so the first question to ask of an entry is what the
nearest arc of **any** kind is, including the end's own.

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
whatever it was drawn to enclose is left open to what lies outside. **None of them on this map**,
down from 61 as 1b-bis, 2c, 2c-ter, 3a-bis, 3b, 4d-bis, 4e and 4f took away the causes one by one,
and the twenty longest are named with their coordinates so that `--debug-svg` can find them by
`data-arc-id`.

**This list was never 61 questions.** Of the 24 that stood after the fused faces were settled, 19
were debris from the needles that return to a vertex, 3 more from the needles that reverse without
returning to one, and the last 2 were overshoots past a crossing. Four defects wearing 24 hats.
That is the strongest argument in this plan for reading a symptom back to its cause rather than
working down the list it produces.

### 4d-bis. Prune an arc with nothing on the far end of it

The last two entries on that list were **overshoots**, and they are worth the walk because the
chain that makes one runs through half this document.

Three borders meet south of **Masikh** and none of them quite meets the others. As drawn, arc 769
ends at 4482.9507, 1829.7666, arc 1081 starts at 4483.3506, 1829.6366 and arc 1086 starts at
4482.3506, 1829.6366. The three distances between those ends are **0.42, 0.61 and 1.00**, so 3a
joins one pair and leaves the third end for 3b, which welds it onto arc 1081's body and splits it
there — making node 2171 the three-way junction it ought to be. So far so good.

What 3a leaves behind is the defect. Moving arc 769's end and arc 1081's start onto one node puts
arc 1081's first 1.39 units back across the line arc 769 came in on, so **the two cross 0.19 units
from the node they now share**. 4a nodes that crossing and cuts both arcs; the two pieces beyond it
join the same two nodes with a sliver of hydraulic radius 0.022 between them, so 4f drops one as a
duplicate — and the survivor has nothing on its far end.

| | |
| --- | --- |
| `n9294 --11377 (2.61)--> n9563 --11501 (1.09)--> n2171` | the junction, degree 3 |
| `n9563 --11378 (0.19)--> n2161` | the overshoot, degree 1 |

So **a dangling arc shorter than the weld tolerance is pruned**, repeated to a fixpoint because
the arc behind one can be left dangling in its turn.

**The bound is the rule and not a detail of it.** Below 0.5 units an overshoot is inside the
distance at which 3a already declares two drawn ends to be the same point, so there is nothing in
it. A longer dangle is a real failure — a chain that never closed, a region that got no face — and
pruning it would delete the evidence instead of the defect. **Prabang's border dangled for 44
units and reading that report is how it was found.** So the long ones stay and 4d goes on
reporting them.

Measured, and unusually tightly: **exactly 2 arcs on this map have a dangling end and they are
exactly the 2 arcs 4d still had to report**, 0.012 and 0.19 units. Pruning settles in one round
with nothing behind them, the arcs that separate nothing go from 2 to **0**, and **no country
moves at all** — net 0.000 square kilometres over 1,590 rows — because 0.2 units of line that
enclose nothing cannot take any ground with them.

A dangling node — one with a single arc end on it — makes that arc a bridge by itself, and
there were 9 before 3a-bis. Counting them is the cheapest way in: each names an arc and a
coordinate to look at. One of the 9 was the arc end 3d reported as unresolved, so the
12.48-unit gap and the Kovenda and Bopedi fused face were one defect rather than two, and 3b
closing the loop settled both. The short ones are now pruned by 4d-bis below; a long one still
reaches this report, which is the point of the bound it is pruned under.

That list turns out to be the inventory behind three reports that had looked like three
separate problems:

| symptom | what the chain did | where it shows |
| --- | --- | --- |
| two countries share a face | failed partway, so the region is not divided | 5b, now none |
| a country gets no ground at all | failed completely, so the region never closes | 4's area check, 0.0130% |
| an arc end resolves to nothing | fell short of what it should meet | 3d, 1 end at 12.48 units |

Every one of the fused faces has a bridge beside it - Deseret and Arizona on arc 8377,
Gwennland on 11499, Kovenda on 9496, Bactria and Sogdia on 8565, Vantong on 790, Tuscany on
948, Kalimantan on 11663, 11625 and 11637 - and arc 9496 sits at 3704.35, 2970.64, which is
the unresolved end from 3d. Six of those pairings have since been settled by the rule the
bridge pointed at, arc 790 and Vantong by 3a-bis measuring a collapsed stub by what it
encloses.

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

**4e. Arcs of no length are a second, smaller defect of the same family, and were at first
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

**What counts as no length is the noding grid.** A closed arc goes when the grid puts every one
of its coordinates at its own node, which reaches 72 arcs where asking for coordinates exactly
equal reached 54. An *open* arc that small has its two nodes brought together instead, and then
goes the same way.

**That is not the merge item 8 ruled out, and the difference is the whole argument.** Merging any
two nodes within a grid step of each other costs twelve countries their ground, because the drawn
geometry never passes through the precision model and two nodes that close together are two real
points in the data. The two nodes at the two ends of *one arc* are not two such points: they are
already joined by that arc, so bringing them together is an **edge contraction**, and contracting
an edge cannot change what is connected to what. Only the arc's own length goes, and the grid has
already said that length is below what it can represent. 55 arcs, 30 nodes; 25 of the 55 had
their two nodes at one identical coordinate and the exact merge was already handling those, so 30
arcs are new, the longest 0.000166 units.

**These arcs are made rather than drawn.** 4a makes every crossing point precise and nothing else
ever passes through the model, so a crossing computed where a vertex was already drawn lands a
fraction of a grid step *beside* it instead of on it. Arc 11706 joins the noded node 9560 at
2440.3538, 857.6366 to the drawn node 633 at 2440.35376, 857.636597 and is 0.00004 units long.
The 4-decimal coordinate against the float one is the signature.

**This is what the 0.301% was**, or 95.7% of it. An island shared between Avanarsuaq and
Qausuittuq has a coast of three arcs, 2317 units of it, with a substate border across the middle
dividing the two countries, and it should trace as two faces. Node 633 has nothing on it but the
0.00004-unit stub, so the stub is a bridge by itself. The traversal came down the 679-unit arc
into node 9560, took the stub first in the departure order, walked to the dangling node and
straight back, and from there retraced every arc it had already walked in the other direction:
one ring of ten half-edges with a signed area of exactly **zero**, bounding nothing, and the whole
landmass inside no face.

| | before | after |
| --- | --- | --- |
| land inside no face | 17,610.0 (**0.301%**) | 759.0 (**0.0130%**) |
| faces traced | 6,989 | 6,991 |
| arcs separating nothing | 25 | 24 |
| unaccounted boundary of 7 | 0.420% | 0.417% |

That last row is not an improvement and should not be read as one: the count of unaccounted
segments is **5,671 either way**. Only the share moved, because the two new faces put more
boundary into the denominator. The commit for this change quotes the row as 0.431% to 0.417%,
which took the baseline from before 3a-bis rather than from before this; 0.420% is the figure
this was measured against.

Of 1592 rows four move and none loses ground: Avanarsuaq 528,098 → **675,591** against 680,444
expected, Qausuittuq 534,615 → **587,877** against 591,857, and Nunangats gains 200,754, which is
*exactly* the sum of its two substates' gains. The shortfall now sits under the 0.1% the check
reports at, so 4's area error no longer fires.

**The signature is gone rather than reduced.** Scanning every ring for the pattern that caused
this — a ring that walks each of its arcs in both directions — finds none afterwards where there
was one before. So this was a single instance and not a class, and the 759 units left is a
different failure mode with its own cause still to find.

The test is metric — no coordinate of the arc reaches one grid step from its start — rather than
the exact mirror of the closed-arc test above. Asking whether the grid rounds every coordinate
onto the start node catches 53 of the 55 and misses two, because whether two points a fraction of
a step apart round together depends on where in the cell they fall. Both give identical output
here, so the two extra arcs change nothing measurable; the metric one is simply the rule that
means what it says.

Seven of the contractions leave a sibling behind: another arc joined the same two nodes and is
longer than a grid step, so it becomes a closed ring rather than going. All seven are tracer loops
of 0.18 units or less on nodes of degree four or more, where two real arcs already meet and
nothing is hidden, so they are left alone.

**4f. Drop an arc that is another arc drawn a second time.** Two arcs joining the same pair
of nodes and running along each other the whole way are one line drawn twice. Both copies
leave each of the two nodes they share, the four half-edges interleave in the order 4b sorts
them into, and a traversal arriving along one of them takes the other as its next turn and
reverses. Whatever it then walks into goes into the outline of the landmass rather than into a
face of its own. That is what left **Siam, Dumarune and Sulka** with nothing, where a border
was copying the coast, and what made **Mauretania and Altavia** one face, where a border was
copying another border.

**Told apart by what the two enclose between them**, as an area over the length of the loop
they make: one line drawn twice encloses nothing, two routes enclose a lens. That is the
hydraulic radius of 2c for the third time. Measured over all 166 pairs of arcs that share both
their nodes: 128 of them come to 0.1243 at the widest, the next is 0.2138, nothing lies
between, and the rest run from there to 12.78 and are alternative routes. The gap is narrow,
1.7×, but it is the only break anywhere below 0.2, and the same 0.15 sits inside it.

Two thirds of the 128 are one border copying another: 67 border on border, 26 border on coast,
11 substate on coast, 9 substate on substate, 6 border on substate, 4 coast on coast, 4 lake
on lake, 1 border on lake. 125 arcs go, 25.84 units of line. The coast is kept over a border
where the two disagree, since a border cannot divide land from sea; otherwise the lowest
identifier is kept, so the same graph always reduces the same way.

**Length is no part of this test and cannot be.** Dumarune's duplicate is 0.21 units long and
Sulka's 0.0002, far below anything that could be told apart from a border merely touching what
it meets by how long it ran. What makes a pair provable is the two shared nodes: noding has
already found that the two lines meet at both ends.

Two earlier attempts are worth recording. A geometric trim, cutting any stretch of border that
stays within 0.02 of the shore for at least half a unit, finds 10 stretches and recovers Siam,
but it cannot see Dumarune's 0.21 or Sulka's 0.0002, and run *before* 2c-ter it cuts away the
one border dividing Maharath from Sanjan and the two come out as a single face. A Hausdorff
distance between the two arcs, at 0.1, works for the border-on-coast pairs but had to be
measured on those alone; the sliver test replaces it and covers every kind.

**The cause upstream is in the weld, and is not repaired.** Welding attaches a loose end to
the arc it lands on by cutting that arc at the junction, and `AddPiece` keeps the drawn vertex
next to the cut. Where the end overshoots, that vertex is left *beyond* its own node and the
piece doubles back: Mauretania's border overshoots node 819 by 0.18 units, which is 0.18 units
of line lying on top of the border that already ran there, and 4a then notes the collinear
crossing, splits both and leaves two identical stubs. Arc 9021's last segment heads at 180°
where the one before it heads at 63°, which is how the defect shows in `--debug-svg`.

Trimming that overshoot at the weld was measured and dropped: of 5,306 vertices next to a
split, 265 double back, and their distances to the node run continuously from 0 to 35.8 units
with no break — most are ordinary sharp corners that have to stay. Where the junction's foot
lands on the segment is no sharper: Mauretania's sits 0.092 along a 2-unit segment. So the
overshoot is left alone and the copy it creates is removed instead.

A separate hazard found looking for this one: **nodes where two half-edges leave at genuinely
equal angles**, where the order falls to `ThenBy(arc.Id)` and is therefore arbitrary. That
report read 109 when it was first taken. 4g below is what came of chasing it.

**4g. Drop an arc that comes back to its node along its own line.** The order 4b sorts a node's
half-edges into is only a cyclic order if each of them leaves in its own direction. Two that
leave along the same ray have no order recoverable from their first vertex, so the sort falls
through to `ThenBy(arc.Id)`, which is not a direction at all — and worse, is not stable, since
4a mints identifiers as it splits arcs at crossings and one shape added elsewhere on the map
can renumber the pair and silently swap the turn. One wrong turn there sends a face onto a
half-edge another ring has already taken, which is the warning 4b carries and a face left
unclosed.

**Measured again after everything above, the 109 are 2** — and both of them are *one arc tied
with itself*. The figure was taken before the needles of 1b-bis, the collapsed stubs of
3a-bis, the duplicates of 4f, the contraction of 4e and the overshoots of 4d-bis, every one of
which removes a reason for two arcs to leave a node along one ray. Those five fixes took the
other 107 with them without being aimed at them.

**What survives is not orderable, which is the finding.** Each is a closed arc that leaves its
node, runs out to a point and comes back over itself — one line drawn twice inside a single
arc, where 4f catches one line drawn twice as two arcs and cannot reach this because it only
looks at pairs sharing two nodes and this one shares its node with itself. Read forwards and
backwards such an arc gives *the same sequence*, so the remedy the paragraph above reaches for
— comparing the two further along than their first vertex — answers "equal" at every vertex.
There is nothing to compare. So the arc goes instead of being sorted.

**The test is exact and wants no tolerance**, which is what makes it safe where 3a-bis's
enclosed radius is not: of the 10,032 arcs in the traced graph and the 5,599 closed ones among
them, two read the same both ways.

| arc | kind | points | length | enclosed radius |
| --- | --- | --- | --- | --- |
| 11183, at node 363 | substate | 3 | 0.00024 | 0 |
| 11589, at node 756 | coast | 3 | 0.00057 | 0 |

The radius test would need a kind restriction to stay safe — 1,326 closed coast arcs score
under its threshold — and that restriction is exactly what hides the coast one. A palindrome
needs no such hedge: an island or a lake shore is never its own reflection.

It runs here and not in 1b-bis or 3a-bis **because neither arc exists when those run**. Before
4a nodes the crossings the graph holds 9,824 arcs with 11,128 the highest identifier;
afterwards 10,138 with 11,685. 4a's own splitting makes them. It runs before 4d-bis prunes the
dangles, so that anything left with nothing on its far end goes in the same pass.

**Every output file is byte-identical with and without it**, which is the honest result for a
hygiene fix: the two ties were landing on the benign side of the arbitrary order. Tracing one
through shows why. One half-edge forms a single-edge ring of zero signed area, which is neither
shell nor hole and is discarded — the outlines bounding no face go from 5,168 to 5,166 — and
the other is absorbed into a neighbouring ring as a spur enclosing nothing. For a spur that
accounting is right, since a spur has one side. But nothing *chose* that; the order the two
half-edges happened to be added in did, and `arc.Id`, the nominal tie-break, cannot tell them
apart at all. This removes the coin rather than calling it.

**4b now reports any tie that is left**, naming the node and the two arcs. It is silent on this
map. Reported for every tie rather than only for two different arcs, because after 4g a tie of
either kind is the same hazard with the same remedy — a closed arc whose two ends leave
collinearly without retracing each other is orderable in exactly the same way — and silence
there would be a blind spot where a warning costs nothing.

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

Both pairs named above have since been repaired upstream — Bopedi by 3b closing a loop the weld
would not let close, Prabang by 3a-bis measuring a collapsed stub by what it encloses — so the
rule now has three fused faces to act on rather than seven, and no country is left with nothing
by it. The figures in this section and the verification below were measured when the rule went
in and are left as they were.

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

A country holds several faces wherever a substate border cuts through it, so the faces are
dissolved back together — **by walking the arcs that bound them, not by unioning their
geometry**.

This used to hand the faces to `UnaryUnionNG` on the grounds that unioning geometry which
already shares its edges exactly follows those edges rather than recomputing them. **That was an
assumption, it was tested in 7's item 4, and it was wrong.** A general overlay re-nodes
everything it is given and writes a new edge wherever it decides two are not quite the same.
Emitting each face as its own polygon, so that no multi-face union happens, takes the
unaccounted boundary from **5,567 segments to 2** — the overlay was not one cause among several,
it was the whole of what was left.

The walk is the one 4b uses, restricted. A half-edge whose other side belongs to the same
country is **interior** and drops out in pairs; what remains is the boundary, chained by the next
way out of a node, clockwise from the way back — except that a half-edge the country swallowed is
**stepped over** rather than taken. That is what makes the walk hug the outside of the region
instead of turning into it at the first internal border. Nothing is recomputed, because no
coordinate is ever calculated: an arc either bounds the country or it does not, and the ones that
do are strung together as they are.

| | union | arc walk |
| --- | --- | --- |
| shapes assembled | 6,122 and 719 | **6,122 and 719** |
| shared exactly | 267,760 | **270,536** |
| unaccounted, national | 5,567 (0.410%) | **5 (0.000%)** |
| unaccounted, `-medium` | 4,254 (1.632%) | **4 (0.002%)** |
| unaccounted, `-low` | 3,711 (3.684%) | **4 (0.004%)** |
| national assembly | 10 s | **1 s** |

205 of 1,590 countries move and **none by as much as a square kilometre** — the largest is
Pathania by 0.444 on 63,906 — with a net of −0.207 over the lot. That is the overlay's edge
rewrites being undone. **The coarse outputs are now exactly shared too**, which this plan said
could not happen until 6d; 6d was already in, and the union was the only thing breaking them.

Two things the implementation got wrong first, both worth keeping. The orientation test used
NetTopologySuite's winding rather than the shoelace 4b sorts rings by, which called every
country's outer ring a hole and gave 325 shapes instead of 6,122. And the angular order of the
half-edges at every node was rebuilt once per country — 2,802 passes over 10,000 arcs — which
made the walk four times slower than the union it replaced; built once for every set between
them, the same output takes 1 second instead of 44.

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

**A coordinate goes only if every arc carrying it offered it up**, and that is the whole of
step 2 rather than a detail of it. Pooling the answers arc by arc does not work, because a
minimum count is a promise about one arc and dropping by value breaks it wherever two arcs
share a coordinate. Arc 1477 is a five-point island, floored at six, so it offers nothing at
all; arc 9922 is 7,777 points of mainland coast that touches it at one vertex and drops it
there, and the island lost it too. **Six rings died that way** - three of Nunangats', and
Windmark's, Wanka's and another of Nunangats' - and the island is always the one that cannot
afford it. Keeping them costs 14 coordinates at 0.001 and 27 at 0.01.

It also settles the nodes, which used to need a pass of their own subtracting them from the
set afterwards. No arc offers up its own ends, so a node is always declined by somebody
whatever another arc does with the same point in its own interior. That pass is gone.

One floor, and it is not the one that keeps a shape alive:

- **An arc that closes on itself keeps at least 6 points** (`ClosedArcMinimumPoints`). An
  island is one closed arc, and simplifying one down to a triangle loses it; 6 is the floor
  the old ring-based simplifier used, kept so the coarse outputs lose no more small islands
  than before. Note what kind of rule this is: a triangle still encloses ground, so no
  guarantee about enclosing ground would save an island from becoming one. 6 is about **how
  much of an island is left**, not about whether it is left.
- **An arc that does not close on itself keeps 2** (`OpenArcMinimumPoints`), which is its own
  two nodes and nothing between them - no floor at all, since those are what the simplifier
  already refuses to go below. **A ring of k such arcs comes out as a k-gon over its nodes**,
  and at k of two that is a line: Gibraltar is one border arc and one coast arc and once
  vanished whole at 0.01, taking the country out of the file with it; Macau is the same two-arc
  shape and came out as three points on one line.

  A floor of 3 was measured first and does fix both, and it was the wrong instrument. It buys a
  vertex on all 4,433 open arcs to avoid the guarantee below raising one on four, and costs 19
  KB of `out-low.json` for nothing the guarantee does not already promise. A blanket floor is a
  guess about every ring made from no ring in particular, and it cannot be a proof anyway -
  three points lie on one line as easily as two. The rings are what know, so the rings are
  asked.

- **A ring left with too little to close is omitted, not kept unsimplified.** Keeping it
  would be the one thing that breaks sharing, since its neighbour would have dropped those
  coordinates and it would not. It used to cost 8 rings of 6467 at 0.001 and 10 at 0.01;
  with the rule above and the guarantee below it **costs none at either tolerance**, and what
  it still catches is the two rings that enclosed nothing before simplification ever ran -
  Anian's islet beyond the rim and Namaquab's lake of 1.3e-7 square units.

**And a ring that would come out enclosing nothing has the floors of its arcs raised until it
does not.** That is the guarantee, and with the open floor at 2 it is the only thing standing
between a two-arc country and a straight line: simplify, ask every ring that enclosed ground
whether it still does, raise the floors of the arcs of any that does not, and simplify again.

It ends, and for a reason worth stating. Raising a floor only ever keeps more, and the cap is
the arc's own length - at that floor nothing is removed from the arc at all, so a ring made of
such arcs is exactly what it was before simplification, and every ring asked about enclosed
ground then. An arc already at its length drops out of the asking, which is also what stops a
ring that can never be saved from being asked about for ever. Floors double rather than step, so
it settles in a few rounds whatever the arcs are.

It asks with the same two tests that actually drop a ring, in `Filter`, so the floors cannot come
to protect something different from what is at risk. And the rings to ask about are taken once
for the run rather than once per tolerance: per tolerance that cost 20 seconds of a 120 second
run, and this way it costs nothing measurable.

**The loop and the question are kept apart**, which is what makes it testable. The raising lives
in `ISimplificationService` with the rest of the simplifying - it takes each ring as the indices
of the lines it is made of, which is all it needs of one - and what counts as a ring holding
together comes in as a predicate from `ConvertCommand`, which is the only place that knows the
answer involves six decimals and a shoelace. The service never sees a ring's geometry and the
command never owns the loop.

**On this map it raises four arcs**, all of them at 0.01 and all in one round: Gibraltar's
border and coast and Macau's, which are exactly the arcs of the two rings that would otherwise
come out as a line. At 0.001 nothing needs it. Pushed harder, with the closed floor dropped to 2
as well so that the islands are at risk too, it raises 577 arcs at 0.001 and 3,682 at 0.01,
still in one round each, and every ring stands.

Five tests cover the loop itself, with the ring's answer stubbed so each is about the raising
and nothing else: that it raises nothing when the starting counts are already enough, which is
what must hold on a sound map; that a ring which will not hold together has **every** line of it
raised, since nothing tells the loop which line the ring wanted; that it stops rather than
climbing for ever when no count can save a ring; and that the counts double, going 2, 4, 8 for a
nine-point line where stepping by one would have taken six rounds. Disabling the raising fails
three of them. The forced run above is the rest of the evidence, end to end.

| | 0.001 | 0.01 |
| --- | --- | --- |
| rings standing, before | 6,477 of 6,480 | 6,472 of 6,480 |
| rings standing, after | **6,480 of 6,480** | **6,480 of 6,480** |
| countries written with no geometry | | Gibraltar, now none |
| coordinates kept back by the shared-coordinate rule | 14 | 27 |
| arcs the guarantee raises | 0 | 4 |
| rounds it takes | — | 1 |

**No country is missing from any output any more**: all three files carry 1,575, where
`out-low.json` carried 1,574. The full-detail files are byte-identical, since simplification
never touched them. At the two coarse tolerances the residue 7 reports rises from 4 unaccounted
segments to 5 and from 0 segments used twice by one country to 1, which is not a regression but
the opposite: those are the Nunangats and Chernorus needles and Bruni's triangle, which exist at
full detail and were being simplified out of existence rather than reported.

**The guarantee is also what let the open-arc floor go back to 2**, which is worth recording
because the floor was put at 3 first and measured working. Against that:

| | floor 3 | floor 2 and the guarantee |
| --- | --- | --- |
| `out-medium.json` | 5,650,092 bytes | 5,645,359 |
| `out-low.json` | 2,347,124 | **2,328,027** |
| `out-low.topojson` | 1,765,212 | **1,755,967** |
| arcs given an extra vertex | 4,433 | 4 |
| rings standing | all | all |

19 KB of the low output, 0.8%, and it buys nothing: both configurations keep every ring and
every country. The full-detail files are byte-identical either way, since simplification never
touches them.

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
- **6d is done: see below. 6e is done: see below.**

**6e. The partition is written as a topology as well.** This is what the arc graph was built
for — the goal at the top of this plan is that TopoJSON should *fall out of the representation
rather than having to be recovered from it* — and that is what it does. An arc exists once, the
two countries either side of it refer to the same one by index, and nothing is deduced by
comparing coordinates.

**One file per detail level, not one per layer.** Both layers go into each as objects,
`countries` and `substates`, over one set of arcs, because that is the thing the format is for: a
national border is also the edge of a substate, and here it is one arc with two references rather
than the same line written twice. It is also why `out.json` carries segments used three times and
the topology does not.

| | GeoJson | topology |
| --- | --- | --- |
| full | `out.json` 28.6 MB | `out.topojson` **16.4 MB** |
| medium | `out-medium.json` 5.6 MB | `out-medium.topojson` **3.7 MB** |
| low | `out-low.json` 2.3 MB | `out-low.topojson` **1.8 MB** |

**9,978 arcs, 3,897 of them referred to more than once.** Quantized at a millionth of a degree,
which is the six decimals 6c writes, so the two files carry the same numbers and a consumer
turning one into the other gets the other. Positions are delta-encoded under a `transform`, as the
specification requires of a quantized topology.

**Simplification costs nothing extra**, which is 6d's doing. Which vertices go was already decided
once per arc, so a coarse topology is *the same rings over shorter arcs*: no reference changes, and
the two sides of a border stay the same line.

**Carrying the rings this far needed one change to 6a's unlooping**: it cuts at a repeated *node*
now rather than at a repeated coordinate, so that each lobe is still a run of whole arcs and can be
named as one. The two agree here, and the measurement is why that is trusted rather than assumed:
**3 of the 7,244 dissolved rings need cutting and all 3 pinch at a node**, which is what 4a
guarantees for a crossing and 1b-bis for a tracer touching itself. A pinch inside an arc would be
missed, and the polygon it made would then fail 6a's validity check and be reported rather than
quietly written. The GeoJson is byte-identical after the change.

**Verified by decoding the files cold** — undoing the transform and the deltas, reassembling rings
from arc indices — and comparing against the GeoJson of the same data. **Zero geometry differences
in every layer of every file.** In the national layer of the full topology 1,224 of 1,225 countries
are identical *including direction*, and the one that is not is **Marmoria, where the topology is
the correct one**: its shell is the single clockwise exterior ring in the GeoJson, which is the RFC
7946 nit this plan records below.

**The specification says nothing about winding**, so this follows the GeoJson beside it —
counter-clockwise shells, clockwise holes. The trap worth recording is that 4b and 6a sort rings in
the *source map's* coordinates, where y runs down the page: unprojecting turns y the other way up
and every ring with it, so the winding has to be taken again in output coordinates rather than
carried over. The one ring that was left wound against its role is Anian's zero-area spike, which
has no winding to get right and is no longer written at all: it is an islet drawn outside the rim,
and 7's item 4 has it.

## Intermediate output for inspection — `--debug-svg`

The stages between the input and the GeoJSON are not otherwise visible: a face is a list
of directed arcs and a weld is a node identity, neither of which can be looked at. `convert
--debug-svg` writes four SVG files beside the output, named after it, **in the coordinates
of the source map and with no projection applied**, so each one can be opened on top of the
file it was read from.

| File | Stage | Contents |
| --- | --- | --- |
| `<out>-arcs-before.svg` | after 3, before 4 | every arc of every kind, and the rim |
| `<out>-arcs-after.svg` | after 4 | the same, with 4a's splits and 4e and 4f's removals |
| `<out>-faces.svg` | after 5, national pass | every traced face as one path, shell and holes as subpaths |
| `<out>-dropped.svg` | after 6a | only the faces left out because their ring crosses itself |

**One arc file per stage, not one per kind, and the stages are the point.** There used to be a
`-coast.svg` and a `-borders.svg`, both written before `BuildFaces`, and **4a is the first thing
`BuildFaces` does**: it splits arcs at their crossings and mints new identifiers as it goes. On
this map the highest identifier goes from 11,125 to 11,717 and the count from 9,824 to 10,048, so
every arc named by a report after that point was missing from the only file there was to search
in. The five bridges 4d reports — 11204, 11271, 11273, 11395 and 11421 — are all above 11,125,
and the border file's own identifiers stopped at 9,915. **Anything 4 or 5 names belongs in
`-arcs-after`; anything 1b, 2c or 3 names belongs in `-arcs-before`**, and an identifier that
appears in neither has been split or dropped in between.

**All four files are framed on the source document's own view box**, used verbatim, so they open
on top of the input and on each other, and the stroke width that follows from the frame is the
same in all four. The two arc files also carry the rim, which leaves the whitespace around it
intact — 139.9 units to the left, 140.5 to the right, 616.3 above, 369.7 below. Each kind has a
group of its own — `rim`, `coast`, `lakes`, `substates`, `borders`, `nodes` — so one file can be
read a layer at a time, and the colour and stroke sit on the group rather than on every line.

Shared framing is a **trade for the dropped file**, which holds a handful of faces and often tiny
ones: the two on this map are 0.2 units across, and fitted to its own contents it showed them
filling the frame. Framed on the document they are specks, so it now says where they are rather
than what they look like.

The view box comes out as 6408.6406 × 4731.3447 against the source's 6408.64086 × 4731.34476,
because the SVG reader parses it as `float32` and the source's digits are finer than that carries.
It is the same reason an arc coordinate reads 2440.353759765625. The framing is off by 0.0002
units in 6408 and the geometry is not affected, since it comes through the same parse either way.

Each arc carries `data-arc-id` and `data-kind`, each node `data-node-id`, and each face
`data-face-id` and, where it has one, `data-owner` — so a face or an arc named in a log
line or a warning can be found in the file by searching for its identifier.

The faces are written **between the national and the substate pass**, because a face inside
a substate belongs to a country and to a substate both and the second pass overwrites the
owner with the substate; that is the only point at which the faces carry the national
owner. A face with no `data-owner` is grey: the ocean, a lake, or ground the border network
enclosed that nothing claims. Face colours otherwise come from the identifier by golden
angle, which is only there to tell neighbours apart.

The nodes are much of the point of the arc files. Welding is what the arc graph rests on, and a
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

   **That guess was right, and it was the whole of it rather than a part.** The control the
   paragraph above calls for - emit each face as its own polygon, so that no multi-face union
   happens - takes the residue to **2 segments**. 6a now walks the arcs instead, and the check
   reads **5 unaccounted, 0.000%**, with the coarse outputs at 0.002% and 0.004%. The figures in
   this item are left as they were measured, since the reasoning that got to them stands; the
   current ones are in the table at its end.

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

   That last sentence is no longer true, and it was wrong about the cause rather than about the
   fact. 6d - simplification deciding once per arc - was already in when it was written; what kept
   the coarse outputs from being shared was the union, the same as everything else here. They now
   read 0.002% and 0.004% unaccounted.

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

   (Those were the figures when the union was still running. They now read 5 and 4; the table is
   kept as measured because the reasoning around it refers to these numbers.)

   `MaximumUnpairedShare` **was** 10%, set to catch a break rather than the residue above, and
   deliberately loose because the residue was not understood. It is now **0.1%**: 25x the worst a
   sound run produces and 340x less than a real break, where thinning every ring on its own
   leaves 34% of the boundary unpaired.

   **Measured again twice since.** After 2c-ter and 4f, which recovered seven countries between
   them, the same check read 265,436 shared and 6,892 unaccounted — 0.513%, down from 0.887%, with
   the coarse outputs at 2.024% and 4.538%. After 6a stopped unioning:

   | | national | `-medium` | `-low` |
   | --- | --- | --- | --- |
   | segments | 1,358,890 | 258,395 | 98,176 |
   | shared exactly | 270,536 | 52,108 | 17,913 |
   | **unaccounted** | **5 (0.000%)** | **4 (0.002%)** | **4 (0.004%)** |

   The 5 are two places and neither is a sharing failure: **Agurania** with 2 segments over 0.029
   degrees and **Bruni** with 3 that make a triangle of no area, sides under a milli-degree. Both
   are the output's six decimals showing. The figures earlier in this item are left as they were
   measured; these are the current ones.

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

   **Settled, and the writer was the problem rather than the part that worked.** Every ring is
   wound against the convention in memory — 6,480 of 6,481 in the national layer, the odd one out
   enclosing no area — and that is not arbitrary at all: 4b and 6a sort rings in the *source map's*
   coordinates, where y runs down the page, and unprojecting turns y the other way up and every
   ring with it. The 821 quoted above was measured when 6a still unioned its faces and the overlay
   chose the winding; it is all of them now. So all of them need turning on the way out and the
   only question is who does it.

   It was NetTopologySuite's serializer, and it got it wrong: **handed 0 clockwise shells it writes
   3.** Orienting before it is not enough — doing only that took the file from 1 clockwise shell to
   3 — so it is told `RingOrientationOption.DoNotModify` and the rings are turned in `Orient`,
   where it is a shoelace and a reversal. That came out of measuring what went in against what came
   out; reasoning about it got the answer backwards.

   | national layer | before | after |
   | --- | --- | --- |
   | clockwise shells | 1 | **0** |
   | counter-clockwise holes | 0 | **0** |
   | segments two countries walk the same way | 807 | **87** |
   | of those, with any length at all | 807 | **0** |

   The 807 were almost all **Marmoria's** borders — Egypt 417, Saconia 257, Phasania 38, Agasymbia
   8 — from its one clockwise shell of 961 points and 32.30 square degrees. The 87 left are every
   one of them **zero length**, a duplicated point rather than a disagreement about direction. All
   five outputs now carry 0 clockwise shells and 0 counter-clockwise holes, and no country's area
   moves.

   **The GeoJson and the topology now agree about all 1,225 countries including direction**, where
   they agreed about 1,224. That needed the two writers to share the rule rather than each keep
   their own: `RingExtensions.GetWinding` answers counter-clockwise, clockwise or neither, with the
   band for "encloses nothing" in one place. Before that the topology reversed Anian's zero-area
   ring where the GeoJson left it — the same disagreement in miniature.

   **The rings that answer "neither" are now dropped rather than written**, which is what the
   paragraph above left open. There are three of them and they have three unrelated causes:

   | ring | what it is |
   | --- | --- |
   | Anian-37's shell | an islet drawn **outside the rim**, 1.2 to 5.2 units past it |
   | Namaquab-0's hole 1 | a closed lake arc of three points enclosing 1.3e-7 square units |
   | Macau-0's shell, at 0.01 | simplification thinning a four-point shell to a there and back |

   **Anian's is the one worth knowing about, because the pipeline is what flattens it.** The
   islet covers 4.15 square units in the source map and sits at x 5576.8 to 5578.8 where the
   drawn rim at that y is at 5575.6 — and the rim is drawn exactly where the projection puts
   the antimeridian, which is itself worth having measured. So every one of its sixteen
   longitudes comes out past 180, `CreateCoordinate` clamps each to 180, and a small island
   arrives as a line 0.11 degrees long. 431 of the map's 1,166,937 distinct coordinates clamp
   that way; this is the only ring all of whose points do. **It is a drawing error in the source
   map rather than anything this code can repair**, and the remedy is to move the islet inside
   the rim or delete it.

   The decision is `Round(ring).GetWinding() == 0`, and it is **the coordinates as written** that
   it asks about, not as held: the writers round to six decimals as they go and a ring can lose
   the last of its area there. The turn in `Orient` still reads the held coordinates, and
   measurement says it cannot disagree - the smallest ring that survives this is Bruni's
   triangle of 8.5e-8 square degrees, whose perimeter admits 1.0e-9 of rounding slack, 82 times
   less than its own area.

   It happens in `Filter`, the method simplification already went through, because that method
   was already deciding the other way a ring can fail to be writable - too few vertices left to
   close - and the two belong together. Two things followed from putting them there:

   - **`Shape.Rings` has to be filtered beside `Polygon`**, one ring for one ring, or the
     topology writes a ring the GeoJson dropped. The old code passed `Rings` through untouched,
     which was a latent disagreement: no hole had yet been dropped by the vertex count, so it
     had never shown.
   - **A country with no ring left is now left out of the GeoJson** instead of written with an
     empty geometry, because that is what the topology already did with it. At 0.01 that is
     Gibraltar, whose shell is already thinned past closing and which was an empty feature in
     the file before this.

   | | before | after |
   | --- | --- | --- |
   | rings enclosing nothing, over the five outputs | 9 occurrences of 3 rings | **0** |
   | segments one country uses twice, substate layer | 2 | **1** |
   | segments used three times or more, at 0.01 | 1 | **0** |
   | countries written with an empty geometry | 1 | **0** |
   | countries whose area moves | — | **0** |
   | the run | 105 s | 105 s |

   The triple-use error was the same ring seen from the neighbour's side: Macau's collapsed shell
   walked its one segment twice and Kanton walked it once, which makes three. That report goes
   with the ring. What is left of the sharing check's spikes is **not** degenerate rings and
   wants saying plainly: two needles inside otherwise sound shells, at Nunangats 0.0001 degrees
   long and Chernorus 0.0017, and one pair of Nunangats shapes that touch along a segment 0.006
   degrees long and should probably be one shape. Those are coordinates inside real rings, so
   removing them would have to happen arc by arc or it would break the sharing it is meant to
   tidy.
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

1. **Done, and it was worth more than this item thought.** Dissolve a country by walking its
   arcs rather than by unioning its faces: the item estimated about 60% of the residue removed
   by construction, from a measurement taken before several of the fixes above. Measured again
   with the control it asked for, the union accounted for **5,565 of the 5,567** — all of it. The
   walk is now 6a and the unaccounted boundary reads **5 segments, 0.000%**.
2. **Done with it.** This item read: 3,535 of the residue existed in no arc at all, so something
   between the faces and the output is inventing coordinates, and the unlooping in 6a is the next
   suspect. The unlooping was innocent; the inventing was the overlay, which no longer runs. What
   is left is 5 segments in two places, neither of them a sharing failure — Agurania with 2 over
   0.029 degrees and Bruni with 3 making a triangle of no area, sides under a milli-degree. They
   are the output's six decimals showing, and `MaximumUnpairedShare` has come down from 10% to
   **0.1%** accordingly, which is 25x the worst a sound run produces and 340x less than a real
   break.
3. **Done. No arc separates nothing**, where 61 did. This item read: each one is a question
   rather than an answer, and reading them as "work on the source map" — as this plan did — is an
   assumption and not a finding. That turned out to be right, and more so than the item expected:
   every one of the 61 had a cause the pipeline could fix, and the four that accounted for the
   last 24 were a needle that returns to a vertex, a needle that reverses without returning to
   one, a line drawn twice and an overshoot past a crossing. The report stays, because it is
   still how a chain that never closes announces itself.
4. **Done, and the number was the whole of the question.** This item read: 109 nodes have two
   half-edges leaving at genuinely equal angles, the order falls to `ThenBy(arc.Id)` and is
   arbitrary, and breaking the ties needs the arcs compared further along than their first
   vertex.

   Measured again, the 109 are **2**. The figure predated the needles, the collapsed stubs, the
   duplicates, the grid contraction and the overshoot prune, and those five took 107 of them
   away as a side effect of being aimed at something else. The two that outlasted them are each
   *one arc tied with itself*, and for those the remedy this item proposed cannot work: an arc
   read forwards and backwards is the same sequence, so comparing further along answers "equal"
   at every vertex. 4g drops them instead, on an exact palindrome test that catches 2 of 10,032
   arcs and needs no tolerance. Every output file is byte-identical afterwards — the ties had
   been falling on the benign side, with nothing but luck holding them there.

   What is left of the item is the report: 4b now warns on any tie, naming the node and the
   arcs, and it is silent. Comparing two genuinely diverging arcs further along remains the
   right fix for a tie between two *different* arcs, and is worth writing when a map produces
   one.
5. **Done.** 6e, TopoJSON, which wanted 1 and ideally 2 done first and got both. Three files
   beside the GeoJson, one per detail level, each carrying both layers as objects over one set of
   arcs; verified by decoding them cold against the GeoJson with no geometry differences anywhere.
   Writing it also turned up the fix for item 6 below: the orientation pass it needs is the one
   the topology already does.
6. **Done.** Two shells reached the file wound clockwise. One was Marmoria, and turning it took
   the segments two countries walk the same way from 807 to 87 — all 87 of them zero length. The
   other was Anian's zero-area spike, which has no winding to get right and is left alone. The
   fix is in 7's item 4 above: the serializer is told not to reorient and `Orient` does it, since
   handed 0 clockwise shells the serializer wrote 3.

   **The degenerate rings are done too**, and they wanted dropping rather than orienting, as this
   item guessed, and they are the three it named: Anian's shell of 17 points, Namaquab's hole and
   Macau's shell at the coarsest tolerance. The write-up is in 7's item 4 above, along with the
   one finding that is not housekeeping: Anian's is an islet drawn outside the rim, which the
   unprojection's clamp at 180 flattens onto the antimeridian, and no code here can give it
   back.
7. **Done. Every country has geometry**, where fifteen had none. This item read: one country
   still has no geometry, Prabang, which is one half of the Vantong and Prabang face that 5b
   reports.

   The window dump found it, as it found the rest: take the country, dump every arc, node,
   departure angle and ring around it, and follow the traversal step by step until it turns the
   wrong way. **Nine cases have now been read this way and gave nine different causes** - a gap
   in the coast open to the sea, one line drawn twice, one border copying another, a stub that
   captured a border's end, a loop the weld would not let close, a gap fat enough to be taken
   for a lake, a stub that kept a vertex of its own and so escaped the rule written for the stub
   before it, an arc the noding grid could not represent dangling off a node, and a tracer that
   drew a stretch of itself twice. Not one of them was guessable from
   the report that announced it, which remains the argument for reading the next one rather than
   reasoning about it.

   **No faces are claimed by two countries any more**, where 33 were at the start. Kalimantan and
   Daha were the last of the kind this plan knows how to fix - one region, two countries, no
   tracer between them - and 1b-bis settled them. The two around Caribbea were never that kind:
   Martinique and Caribbea, and Caribbea and Iouanorre, were each reported at a **full 1.00 for
   both** claimants, which is not a division that failed but two shapes drawn one on top of the
   other. Those went out of the source map on 2026-10-07, and with them the last two entries in
   5b's report. Caribbea gains 1,989 square kilometres, the ground the duplicates had been
   taking, and two rows leave the output; six unrelated countries move by under 0.001%, which is
   the file having been re-saved rather than anything the pipeline did.
8. **Done, and only half of it was right.** This item read: 22 arcs are shorter than the
   0.0001 unit noding grid and 4e does not reach them, because it asks for every coordinate to
   equal the node exactly rather than to within the grid; 18 of the 22 are closed and the other
   4 join two nodes less than a grid step apart, which `MergeCoincidentNodes` leaves alone for
   the same reason, so **both tests want widening to the grid**.

   The arc test was widened and is now in 4e: a closed arc goes when the grid puts all of its
   coordinates at its own node. 54 arcs dropped becomes 72, and no other number on this map
   moves - the land inside no face, the arcs separating nothing, the fused faces and the
   boundary sharing cannot account for all stay exactly where they were. Hygiene, as this item
   guessed, rather than a repair.

   **Widening the node test is wrong, and that is the finding worth keeping.** Merging nodes
   the grid puts at the same point collapses 605 of them rather than 57 and takes the countries
   with no geometry from **6 to 18**, the arcs separating nothing from 41 to 58, the fused faces
   from 11 to 13. Measured two ways round, once with a tolerance of a grid step and once by
   rounding both coordinates through the precision model; both give the same 605 and the same
   18. Those figures were measured before 3a-bis, which has since taken the three baselines
   from 6, 41 and 11 to 3, 31 and 7.

   The reason is that **the drawn geometry never passes through the precision model**. Only the
   crossing points of 4a are ever made precise; the tracer and coast coordinates come off the
   SVG as they are. So two nodes less than a grid step apart are two real points in the data,
   and rounding them together joins chains the map never joined. The grid is a resolution, not
   a tolerance: within one arc it says what is too small to have a direction, between two nodes
   it says nothing at all. What makes it safe in 4e is the condition that the arc be closed,
   since dropping a closed arc can never disconnect anything, where bringing an open arc's two
   nodes together can.

   Which leaves the 4 open sub-grid arcs in place, and they are the right thing to leave: each
   joins two nodes the map really does distinguish.

## Order of work (as planned at the start)

0d and 0e first — they are standalone bug fixes, independently verifiable, and
everything downstream depends on the tracer layer being read completely. Then
0a–0c, then Phases 1–2 (verifiable by writing the coast out as GeoJSON and looking
at it), then 3–4 (verified by face count and dangle count), then 5–6. Phase 6e
(TopoJSON) and 6d (arc simplification) can land after the GeoJSON output is passing
Phase 7.
