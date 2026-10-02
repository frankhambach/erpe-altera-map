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

- **`WeldTolerance = 1.0` units** (≈ 6.5 km) — how far a loose end may reach to find
  something to attach to. It stays below the 1.75-unit linework resolution, so it
  cannot merge genuinely distinct features.

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
`WeldTolerance`, so nothing real is quantised away and snap-rounding robustness is
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
- total length `< WeldTolerance` → keep, and let Phase 3a's union-find merge both
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
in Phase 3 and the lake stays a single face. 379 tracers are trimmed, removing 31.95
units of line in all. A check afterwards reports any tracer still inside a lake: one
remains, by 0.39 units, which is logged rather than forced.

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

**3a. Endpoint ↔ endpoint, with union-find.** Group endpoints within
`WeldTolerance` into one node using a disjoint-set structure, then set the node's
coordinate to the group's centroid. Union-find rather than pairwise snapping
because the current `Snap` sorts by X, `break`s after the first partner found, and
therefore resolves neither chains nor transitivity: A→B and B→C can leave A and C
apart. Handles 314 junctions, and absorbs the short-connector case from 0e.

**3b. Endpoint ↔ segment interior — the dominant case.** For each endpoint still
unattached, find the nearest arc segment within `WeldTolerance`. Split that segment
at the foot of the perpendicular, insert a node there, and merge the endpoint into
it. ~1500 junctions in this file. **Do not treat this as the exception path** — it
is how most of this map's borders meet.

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

**3d. Report, do not guess.** Any endpoint unresolved at `WeldTolerance` goes to a
warning list with its SVG coordinates and, so the report says how far short it fell
and what it probably should have met, the kind and distance of the nearest arc beyond
the tolerance. Expect 7 entries. This list is the manual-review queue for each new
export, and it must stay short — if it grows, something changed upstream.

**3e. Apply the tolerance to endpoints only, never to interior vertices.** The
linework interiors are not defective; only the ends are. This keeps every coast and
border vertex bit-identical to the drawing, and it is why welding is done as a local
operation rather than by snap-rounding the whole coast: a global grid at
`WeldTolerance` would staircase the 19% of drawn segments shorter than 1 unit.

**Verified** against `Altera_Data_Generating_Map(13).svg`: of 2872 loose ends, 257
merged onto each other, 2608 split an arc to attach partway along it — 1025 of those
on the coast — and 7 remain loose and reported. T-junctions outnumber endpoint-to-
endpoint joins by better than ten to one, so splitting is the main path, not the
exception.

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

Two residues remain, both reported rather than papered over:

- **7 lakes of 462 are still not recognised as water**, and one of them is the largest
  lake on the map. That one is explained: it holds the single tracer that survives the
  Phase 2 clip, 0.39 units of it, which puts a border arc in the lake face's boundary. Fix
  the clip residue and the lake follows. The other six are small, 208 units² and below.
- **8 faces are claimed by no country and border none**, so nothing can absorb them. They
  are unclaimed land whose every neighbour is also unclaimed or water.

## Phase 6 — assemble and write

**6a.** Per country, build the `MultiPolygon` from its faces' **arcs**, not by
unioning polygons. An overlay operation here would reintroduce the floating-point
mismatch the whole pipeline exists to avoid.

**6b. Unproject once per unique node, memoized.** Every arc references nodes by
identity, so a memoized `Rescale` + `Unproject` per node guarantees that both
neighbours of a border receive literally the same output coordinates. This is the
step that makes "borders match exactly" a property of the construction rather than
something to verify afterwards.

**6c. Output precision.** `RoundCoordinateSequenceFilter` rounds to 4 decimal
degrees ≈ 0.0017 SVG units, which is ~17× coarser than the Phase 0a noding grid —
distinct nodes would collide and produce degenerate rings. Raise output precision to
6 decimals; the file grows, and the `-low` / `-medium` outputs are where size is
managed. Coarsening the noding grid to match is the alternative, but Phase 0a
rejected that on measurement.

**6d. Simplification must become arc-based.** `VisvalingamSimplificationService`
currently simplifies each polygon independently, which would give the two sides of
a border *different* simplified geometry and destroy every guarantee above.
Simplify each **arc** once and rebuild polygons from the simplified arcs. This
applies to the existing `-low` (0.01) and `-medium` (0.001) outputs.

**6e. TopoJSON.** With nodes, arcs and per-arc face adjacency already in hand, the
writer is a direct serialisation: emit the arc coordinate lists, then each country
as indices into them. Worth doing as its own phase once GeoJSON output is verified.

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
