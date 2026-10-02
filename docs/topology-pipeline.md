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

- **`WeldTolerance = 0.5` units** (≈ 3 km) — how far a loose end may reach to find
  something to attach to. Justified by the 99.59% coverage above, and by sitting
  below the 1.75-unit linework resolution so it cannot merge genuinely distinct
  features.
- **`MouthBridgeTolerance = 2.0` units** (≈ 13 km) — how far across a river mouth
  the two coastlines may be welded. Justified by the 1.60-unit measured maximum.
  Anything beyond this is reported, never welded silently; this is what stops a
  legitimate strait being zipped shut.

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

**3c. River-mouth weld.** For each tracer endpoint that attached to a *coast* arc
at point `P` on ring `A`:

1. Find the nearest point `Q` on a ring other than `A`, within
   `MouthBridgeTolerance`.
2. **Side guard:** require `Q` to lie on the opposite side of the tracer from `P`,
   by the sign of the cross product of the tracer's final direction with `P→Q`.
   Without this the weld can attach to a ring behind the endpoint.
3. Collapse `P`, `Q` and the tracer endpoint into a single node.

This is the step that closes gaps open to the sea. Welding the two coast walls
together at the mouth turns the notch into a *closed* unclaimed region, bisected by
the tracer, so Phase 5c absorbs each half into the country beside it. Without it,
only the half of the notch cut off by the tracer closes; the other half stays an
inlet, leaving one country's coast set back by the gap width.

Roughly 750 welds, each local — which is why this is not done by snap-rounding the
whole coast. A global grid at `WeldTolerance` would staircase the 7% of genuine
coast segments shorter than 0.5 units.

**3d. Report, do not guess.** Any endpoint unresolved at `WeldTolerance`, and any
mouth whose partner ring is beyond `MouthBridgeTolerance`, goes to a warning list
with its SVG coordinates. Expect ~12 entries, 4 of them real. This list is the
manual-review queue for each new export, and it must stay short — if it grows,
something changed upstream.

**3e. Apply the tolerance to endpoints only, never to interior vertices.** The
linework interiors are not defective; only the ends are. This keeps every coast and
border vertex bit-identical to the drawing.

## Phase 4 — node and polygonize

In `ITopologyService`:

1. Feed all arcs — border, substate, coast — to `SnapRoundingNoder` at the
   Phase 0a precision model. This resolves genuine crossings robustly and
   guarantees a valid arrangement.
2. `Polygonizer` → faces. Capture `GetDangles()` and `GetCutEdges()` as
   diagnostics: a dangle after Phase 3 means a border that failed to connect.
3. Record, for each arc, the faces on its left and right. This adjacency is what
   Phase 5c and the TopoJSON writer need.

## Phase 5 — assign faces to countries

New `IFaceAssignmentService`.

**5a.** For each face take an interior point (`Geometry.InteriorPoint`) and find
the containing `Country_Shapes` polygon via an `STRtree`. One match → that country
owns the face.

**5b. Ambiguity, and the key diagnostic.**

- 0 matches → sea, or an unclaimed sliver (5c).
- ≥ 2 matches → the shapes overlap there. Resolve by the larger area of
  intersection with the face. **Also log it:** a *large* face inside two different
  countries means a tracer is missing and two countries have fused into one face.
  That is the main failure mode of this whole approach, and this check is how it
  surfaces. Set a threshold on face area and treat exceedances as errors, not
  warnings.

**5c. Unclaimed land faces → absorb.** Merge each into the neighbour sharing the
longest arc with it. This single rule replaces gap detection entirely: every gap,
enclosed or formerly open to the sea, is by now a closed face that nobody claimed.

**5d. Sea.** Keep the ocean face as a first-class face rather than discarding it.
Coastlines then become shared arcs too, and the topology is fully closed instead of
having every coast as a dangling ring. Emit it as an `Ocean` feature or filter it
at write time.

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
