# CLAUDE.md

Guidance for working in this repository.

## What this is

A .NET 9 CLI (`erpe-altera-map`) that turns an Atlas Altera SVG world map into GeoJson and TopoJSON. One command, `convert`. See `README.md` for usage and the files it writes.

The goal is a **planar partition**: a border exists once, and both countries either side of it are built from the very same `Coordinate` objects. Every design decision follows from that. In particular, geometry is **never** repaired by overlay — no `Union`, `Buffer(0)` or snapping to fix up shared edges — because a general overlay re-nodes its input and writes a new edge wherever it decides two are not quite the same. That one change, replacing a union with a walk over the arcs, took the unaccounted boundary from 5,567 segments to 2.

## How the conversion works

The input is not a partition. The drawn country shapes overlap, leave gaps, and each carries its own version of every border it shares. The pipeline throws those versions away and rebuilds the map from a graph of **arcs** — lines between **nodes** where three or more of them meet — so that a border is one arc with a country on each side.

Everything until 7b happens in **SVG coordinates**, on the fixed precision grid `Program.cs` installs; both are conventions rather than choices a new step may revisit, and the section below says why. `Program.cs` also wires the services through `Microsoft.Extensions.DependencyInjection`.

### The steps, and what implements each

`Commands/ConvertCommand.HandleAsync` runs the whole pipeline in this order. **The numbers are the labels the code comments use**, so a comment that says "3c has already filled the slits" or "5a mints identifiers as it splits" means the rows below.

| | What happens | Where |
| --- | --- | --- |
| **1** | **Read the drawing.** | |
| 1a | Approximate curves and `<line>`, `<rect>`, `<polygon>` as lines, De Casteljau at 0.01 units | `SvgApproximationService.Approximate` |
| 1b | Drop linework under 0.1 units — a length filter, because a point count throws away a legitimate two-point segment and keeps a sub-pixel speck | `SvgApproximationService` |
| 1c | Leave out an element the author hid; `display="none"` says it is not part of the map | `SvgElementExtensions.IsDrawnLinework` |
| 1d | Read `Border_Tracers` and `Substate_Tracers` into arcs on shared nodes, both layers into one graph | `TracerService.AddArcs` |
| 1e | Build the drawn country polygons: snap, close each sub-path, unloop self-intersections, polygonize | `CorrectionService.Snap`, `TopologyService.Close` → `Unloop` → `Polygonize` |
| 1f | Tie each group under `Substate_Shapes` to the country whose shape contains its substates, and make that country a federation of them. It keeps its own name — Ocarasta, not Grand Praire Confederacy — and its shape is never used again: a federation is its substates | `ConvertCommand.GetFederations` |
| **2** | **Cut out what a tracer draws twice.** | `CorrectionService.Collapse` |
| 2a | Cut a needle: a stretch a tracer draws out and back along itself, which no face can lie on one side of | `RemoveNeedles` |
| 2b | Collapse a stretch that repeats *another* tracer onto the copy that is kept, leaving the ends loose for step 4 | `FindDuplicates`, `ApplyRemovals` |
| **3** | **Build the coastline.** | `CoastlineService.AddArcs` |
| 3a | Union the unitary states' and the substates' polygons into the landmass, never a federation's own shape. The two drawings of one coast disagree by a hair in thousands of places, and taking the coast from the federation while the faces went to the substates left islets no substate claimed | `UnaryUnionNG` |
| 3b | Take each shell as one closed coast arc, whole; step 5 splits it where a tracer meets it | `AddArc(ArcKind.Coast)` |
| 3c | Fill a slit: a gap between two shapes that reaches the sea is not a hole but a cut in the shell, and the ground beside it would otherwise be enclosed by nothing | `FillSlits` |
| 3d | Divide each hole at its necks where a border lies at them, then tell a lake from a gap piece by piece — by hydraulic radius, and by whether a border runs the piece's whole length — and keep the lake as its own kind of arc. A gap between two substates that runs into a lake is the same hole as the lake, and judged whole it lost three lakes | `SplitAtNecks`, `GetAreaToLengthRatio`, `GetTracerCrossing` |
| 3e | Cut the needles out of the coast and lake rings, where two shapes disagree about the water's edge by a hair | `RemoveNeedles` |
| 3f | Cut a tracer back to the lake shore, since a border drawn across water would shave a sliver off the lake | `ClipTracers` |
| 3g | Cut a tracer off the sea shore where it was drawn along it, and drop what is left seaward | `ClipShoreRuns` |
| **4** | **Weld the loose ends.** | `CorrectionService.Weld` |
| 4a | Merge ends within 0.5 units into one node, with union-find so a chain resolves whole | `MergeLooseEnds` |
| 4b | Drop the arcs that merging collapses, before the splitting runs, so their ends become loose again | `RemoveCollapsedArcs` |
| 4c | Thread an end into the arc it reaches within 3 units, splitting that arc at a new node. The dominant case by seven to one | `FindSplit`, `ApplySplits` |
| 4d | Express a split that lands on a vertex against *that* vertex, or the next one along is discarded. Ties go to the lower identifier, never to the index's order | `CreateSplit`, `IsPreferred` |
| 4e | Report an end that reaches nothing rather than guessing — reaching further invents a border | `Weld` |
| 4f | Apply the tolerance to ends only, never to interior vertices, which would staircase real coastline | `Weld` |
| 4g | Add the rim — the antimeridian, traced from the projection down both edges and carried a unit past each pole — as `ArcKind.Rim`. After welding, so no end is threaded into a line nobody drew | `RimService.AddArcs` |
| **5** | **Node and trace the faces.** | `TopologyService.BuildFaces` |
| 5a | Node the crossings, making each crossing point precise. This mints new arc identifiers. Then keep only the stretches of rim over land, by even-odd against the coast and lake shores | `NodeCrossings`, `RemoveRimOverWater` |
| 5b | Contract the arcs the grid cannot represent and merge nodes that share a coordinate exactly | `ContractDegenerateArcs`, `MergeCoincidentNodes` |
| 5c | Drop an arc that is another arc drawn a second time, told by the sliver the two enclose. Against the rim the test is 0.01 rather than 0.15 and the rim is the one kept: that cuts the hairlines where Antarctica's coast is the drawn rim, and leaves land drawn past the rim to 5i | `RemoveDuplicateArcs`, `GetMaximumSliverRadius` |
| 5d | Drop a closed arc that leaves its node and comes back along its own line, where both half-edges claim one direction | `RemoveRetracedArcs` |
| 5e | Prune an arc with nothing on the far end of it — an overshoot past a crossing encloses nothing | `RemoveDanglingArcs` |
| 5f | Trace every ring by walking half-edges: at each node, one step clockwise from the way back | `TraceRings` |
| 5g | Assign the outlines — which rings are faces and which bound the ocean — and check the total against what the coastline encloses | `AddFaces` |
| 5h | Report the arcs that separate nothing: both sides in one ring means a chain that never closed | `ReportSeparatingNothing` |
| 5i | Give each face beyond the rim the offset that takes it round the globe, or leave it out where its far side is ground already drawn — the halves of Chukcha and Mammoth Island, which overshoot onto each other | `RimService.Wrap` |
| **6** | **Assign the faces to countries.** | `FaceAssignmentService.Assign` |
| 6a | Tell water from land by what bounds a face, not by how it nests: a face bounded only by lake shore is water | `Assign` |
| 6b | Claim a face by how much of it a country covers, matching faces to countries rather than to single shapes | `Assign` |
| 6c | Absorb an unclaimed land face into the neighbour it shares the most border with | `Assign` |
| **7** | **Assemble and write.** | |
| 7a | Dissolve each country by **walking the arcs** that bound its faces — never by unioning them. The rim is never dissolved, so a country it crosses comes out as a shape either side | `AssemblyService.Assemble`, `TopologyService.DissolveFaces` |
| 7a' | Give each wrapped shape its own copy of the rim arcs it meets, since the two sides are written 360 degrees apart, and return each arc's offset | `RimService.Twin` |
| 7b | Unproject, memoized per SVG coordinate, so both sides of a border get literally the same numbers. The rim goes into the memo first at exactly ±180, and a wrapped shape's offset is added after the memo. Nothing is clamped, and nothing is put on the precision grid: in degrees its 0.0001 is 11 m, coarser than 7c | `ConvertCommand.Unproject`, `UnprojectRim`, `WinkelTripelProjectionService` |
| 7c | Round to six decimals, not four: four is coarser than the noding grid and would put distinct nodes on top of each other. The writer rounds a copy, since the coordinates it is handed are the shapes' own and every later pass reads them | `ConvertCommand.Round` |
| 7d | Turn every ring the way RFC 7946 asks, decided in output coordinates | `ConvertCommand.Orient`, `RingExtensions.GetWinding` |
| 7e | Leave out a ring that encloses nothing as written, and a country left with no ring | `ConvertCommand.Filter` |
| 7f | For the coarse outputs, simplify once per arc and drop by coordinate value, so both sides lose the same vertices | `SimplificationService.SelectRemoved` |
| 7g | Raise the arcs' floors and simplify again until no ring that enclosed ground comes out enclosing nothing | `SelectRemoved`, the fixpoint |
| 7h | Write the GeoJson, and the same partition as a topology sharing one set of arcs | `ConvertCommand.WriteGeoJsonAsync`, `TopoJsonService.WriteAsync` |
| **8** | **Verify.** | `VerificationService.VerifySharing` |
| 8a | Count every segment of the output and check each inland one is used exactly twice, from opposite sides, with the same coordinates | `VerifySharing` |
| 8b | Account for the segments used once — coast, lake shore, or the edge of a face left out — and report what is left | `VerifySharing` |

Step 6 runs once, over the countries the map is finally made of: the unitary states and the substates, with unclaimed faces absorbed into a neighbour of either kind. 7a then runs once per layer: the substates are assembled while the faces carry them, every substate's face is handed to its federation, and the national layer is assembled from that. From 7b on the two are carried together, and each output is written three times over — the national layer, the substate layer and the two combined, which is unitary states and substates and is a partition of the land like the other two. Steps 7e to 8 then repeat at tolerances of 0.001 and 0.01 for the coarse outputs.

`--debug-svg` writes four files in the coordinates of the source map, framed on its own view box, through `IDiagnosticsService`: the arcs before and after face tracing, every face, and the faces left out. Both arc files are written because 5a mints new identifiers as it splits arcs at their crossings, so anything the later reports name by number is absent from the earlier file.

### The models

* `Arc` — interior coordinates plus two *mutable* `Node` references, so welding can re-point an end without rebuilding anything. `GetFullCoordinates()` is start node + interior + end node. `IsClosed` is `StartNode == EndNode`. `ArcKind` is `Border`, `Substate`, `Coast`, `Lake` or `Rim`.
* `DirectedArc(Arc, bool IsReversed)` — a half-edge. `StartNode`/`EndNode` flip with `IsReversed`.
* `Face(Shell, Holes)` — rings of `DirectedArc`, with an `Owner` once step 6 has run and a `LongitudeOffset` of ±360 once 5i has found it beyond the rim.
* `ArcGraph` — the arcs, nodes and faces, and the only place identifiers are minted.
* `Country` holds `Shape`s, and a `Shape` carries both its `Polygon` and the `DirectedArc` rings it was walked from. That is what lets the TopoJSON fall out of the graph rather than be recovered by comparing coordinates.
* `RingExtensions` — the single winding rule. `GetWinding()` answers 1 / −1 / 0, the 0 meaning "encloses nothing" within a band of 1e-12. **Both writers go through it** so neither can disagree with the other about a ring.

The recurring discriminator across the whole pipeline is the **hydraulic radius**, area over perimeter: it has units of length and scales linearly, so it measures thinness and smallness at once and needs no separate area test. It tells lakes from gaps, real rings from slivers, needles from shapes, and collapsed stubs from corners.

## Build, run, test

```bash
C:/Users/frank/.dotnet/dotnet.exe build Erpe.Altera.Map/Erpe.Altera.Map.csproj -v q --nologo
C:/Users/frank/.dotnet/dotnet.exe test Erpe.Altera.Map.Tests/Erpe.Altera.Map.Tests.csproj --nologo -v q
C:/Users/frank/.dotnet/dotnet.exe run --project Erpe.Altera.Map/Erpe.Altera.Map.csproj --no-build -- \
  convert --in "C:/Users/frank/Downloads/Altera_Data_Generating_Map_20261014.svg" --out <dir>/map.json
```

A full run is about a hundred seconds. Write outputs to the scratchpad, not into the repo.

**The test suite passes clean: 1049 tests, 0 failures.** It carried 44 failures for a long time — 43 `Unproject*` in `WinkelTripelProjectionServiceTests` and 1 `GetCoordinates*` in `SvgApproximationServiceTests` — but every one of them was a wrong expectation rather than a defect in the pipeline, and correcting them took the total from 1010 to 1008 by removing two tests that asserted nothing. **0 failing is the baseline now**; a failure is a real regression and none is expected, so do not wave one through as pre-existing, and say the total when reporting ("1049 passing, 0 failing").

## Conventions that will fail the build if ignored

* **Analyzers are errors.** `Erpe.Analyzers` brings StyleCop and Sonar. The ones hit most often: SA1117 (parameters all on one line or each on its own), SA1202/SA1204 (public before private, static before instance), SA1508/SA1513 (blank lines around braces), SA1118 (no multi-line argument), SA1210 (usings ordered), S1144 (no unused private member — so a helper must be used in the same change that adds it), S1244 (no `==` on doubles; use `.Equals` or a range), S3358 (no nested ternary), S6605 (`Array.Exists` over `Any`), S125 (no commented-out code), CA1062 (a public method validates its arguments), CA1859, CA2007, CA1822/S2325, VSTHRD110, CA1861.
* **Nearly every `.cs` file is UTF-8 with a BOM** — 53 of the 59 tracked ones — and the working tree uses CRLF throughout (`core.autocrlf` is on, so git stores LF either way). The six without one are `AssemblyInfo.cs`, `IAssemblyService.cs`, `ICorrectionService.cs`, `ITracerService.cs`, `TracerService.cs` and `SvgApproximationServiceTests.cs`; they never had one, so leave them as they are rather than tidying them in passing, and give a new file the BOM. `sed -i` and most scripted edits strip the BOM, convert the line endings, or both — as does the `Write` tool, which emits neither BOM nor CRLF. Check with `file <path>` after writing a file any way other than `Edit`: a lost BOM is a real change, and an LF working-tree copy makes git warn on staging. Markdown has no BOM.
* **Commits are signed.** Verify with `git log --show-signature -1`.
* **Correction happens in SVG coordinates**, right up to 7b. The defects being corrected are drawing errors: they are uniform in SVG units and vary by a factor of ten with latitude once unprojected, so a tolerance in degrees snaps far harder near the poles than at the equator. Do not move the unprojection earlier, and do not introduce a tolerance in degrees.
* **The precision grid is a resolution, not a tolerance.** `Program.cs` installs a global `PrecisionModel(10000.0)` — 0.0001 units, about 0.65 m here — and only the crossing points 5a makes are ever put on it; drawn coordinates come off the SVG as they are. Within one arc the grid says what is too small to have a direction. Between two nodes it says nothing: merging nodes a grid step apart was measured and joins chains the map never joined, taking the countries with no geometry from 6 to 18. It is never applied to degrees, where its 0.0001 is 11 m: `Unproject` once rounded onto it, a leftover from a pipeline that unprojected first, and every coordinate in the files had four decimals whatever 7c wrote.
* **Geometry is built, not repaired.** A new step that reaches for `Union`, `Buffer(0)` or a snap to tidy shared edges is the wrong step; walk the arcs instead.
* Comments carry the *reasoning and the measurement*, not a restatement of the code. This is the house style and it is why the comments are long. Every threshold says what was measured to choose it and what the figures either side of it were, because a threshold without that is a number nobody can safely change. Match it.

## Invariants to preserve

Breaking any of these is a regression even if the build and tests pass:

1. **Borders are shared exactly.** `IVerificationService.VerifySharing` runs in-pipeline per layer; inland segments must be used twice from opposite sides. Simplification decides **once per arc** and drops by coordinate value, which is what carries one decision to both sides. Never simplify a ring.
2. **A coordinate is removed only if every arc carrying it offers it up.** A minimum point count is a promise about one arc, and removing by value breaks it wherever two arcs share a coordinate.
3. **No ring that enclosed ground may come out enclosing nothing.** `ISimplificationService` raises the arcs' floors and simplifies again until none does.
4. **Rings are wound for RFC 7946** — counter-clockwise shells, clockwise holes, decided in *output* coordinates. The source map has y running down the page, so unprojecting turns every ring over. NetTopologySuite's serializer is told `RingOrientationOption.DoNotModify` because it gets this wrong: handed 0 clockwise shells it writes 3.
5. **The GeoJson and the TopoJSON agree**, geometry and direction, for every country.

## Working style this project expects

**Measure; do not reason and move on.** This is the single most useful habit here, because reasoning has given the wrong answer repeatedly — most sharply over the ring winding, where orienting before the serializer made the file *worse* and only instrumenting what went in against what came out found the cause. Before and after a change, run the pipeline and diff the reports; `cmp` on the written files is often the clearest evidence, and "byte-identical" is a real result worth reporting.

The productive technique for a bad face or a missing country is the **window dump**: take the country, log every arc, node, departure angle and ring around it, and follow the traversal step by step until it turns the wrong way. Nine cases were read that way and gave nine different causes, none of them guessable from the report that announced it.

Four traps worth knowing:

* **Stale binaries.** A build error means the run you just did used the old binary. Check the build succeeded before trusting a measurement — a `--no-build` run after a failed build has produced a whole misleading comparison this way.
* **Instrument, then revert.** Probes go in, get measured, and come out. Verify the tree afterwards with `git status --short`; the working tree normally carries only the user's own changes (a `stylecop.json` deletion and some untracked files).
* **Arbitrary tie-breaks are load-bearing.** Where two candidates are equidistant, or two arcs coincident, the answer has to come from the arcs' own identifiers and not from the order an index happens to return. An `STRtree`'s order changes whenever anything is added anywhere, and a tie decided by it moved borders hundreds of units away from the change that disturbed it.
* **The input file is edited in place under the same name.** It changed on 15 October between one measurement and the next, which invalidated a day's figures; check its timestamp before trusting a comparison against an earlier run.

## State and open items

Measured on `Altera_Data_Generating_Map 20261022.svg`, the export of 22 October. Note the space in the name where the earlier one had an underscore.

* **1225 countries** in the national layer, 365 substates in the substate layer and 1574 in the combined output, every one of them with geometry. 16 federations are made of their substates, four of them — Nunangats, Belgium, Ocarasta and Caribbea — not marked as federations in the country layer.
* **6956 faces** traced, covering 5,854,359.1 of the 5,855,115.7 square units the coastline encloses. 461 lakes, after the holes were divided at 4564 necks into 8788 pieces. 2 unclaimed faces, both absorbed into their neighbours.
* **No arc separates nothing**, and no arc end is left loose.
* The sharing check, over 1,357,342 segments: 271,029 shared exactly from both sides, and **0 wound the same way, 0 used twice by one country, 0 used by three or more rings**. 3 segments (0.000%) are unaccounted for. The combined output is a partition as well — 0 segments used three times and 0 wound the same way, checked by reading the file — where it used to carry Nunangats on top of its own substates.
* **Every ring survives every tolerance**: 6455 of 6455 in the national layer at 0.001 and at 0.01, the floor raised on 2 arcs and on 10 to keep it so.
* **No ring encloses nothing** and none is dropped on the way out. Namaquab-0's hole 1 was, until the unprojection stopped putting degrees on the grid, and Anian-23's shell was until it stopped clamping at longitude 180.
* **0 clockwise shells and 0 counter-clockwise holes** — 6095 shells and 360 holes, all the right way round. Nothing reports this; it is checked by reading the written file.
* **Every longitude lies in [-180, 180]**, in all three resolutions. Nine shapes are drawn past the rim. Anian-23 lies wholly beyond it and is taken round whole; the far parts of Anian-21, -22 and -43 are cut at the rim and taken round, 4 faces in all; the overshoots of Chukcha's and Mammoth Island's two halves land on the other half and are left out, 4 faces and 133.6 square units; Antarctica's hairlines along both edges are cut in 5c. Nothing reports the range either. The southern edge comes out at -89.999998 rather than -90, which is Newton's method's answer at the pole now that nothing rounds it.

The run finishes with **no errors**. Its 27 warnings are the per-case reports of what it corrected — 14 duplicated tracer stretches and 12 tracers cut back off the shore, each named with where it is — plus one that is the first open item below.

Open:

* **2 holes land in the band the gap threshold was chosen to avoid**, between 0.10 and 0.19 on the area-to-length ratio, which was empty on the earlier export. Lakes and gaps are no longer cleanly separated on this one. `CoastlineService`'s constants say what that figure rests on; it wants watching rather than nudging.
* **2 faces are claimed by more than one country**, both too small to be reported individually.
* **3 unaccounted segments**, 0.000% of the boundary, which is the output's six decimals showing rather than a border with one side to it.
* **3 water faces of no area**, triangles 0.003 to 0.05 units round bounded only by lake shore. Water is never written, so they reach no file.
* **One lake is drawn smaller by the substates** than by its country: 57.3 square units against 60.2, at about 1901, 1037 in the source map. That is the drawing and not the pipeline, which gives back every other lake the country shapes have.
* **`Unproject` answers in radians near the centre of the map.** Within about 0.00006 degrees of longitude 0, latitude 0 its Newton step has no denominator, and the fallback returns the iterate unconverted and put on the grid. Nothing on this map lies there, and the run logs it as an error if anything does.
* **A hidden group would still be read.** The visibility rule asks the element, not its ancestors, and hiding a whole layer is what Illustrator does.

Known input errors are fixed in the source SVG by the user, not worked around here — duplicate Martinique and Iouanorre shapes went that way, as did an island present in the Substates layer but missing from the Country layer, and the tracer whose absence left Iveria and Ossetia sharing a face was drawn on 15 October, which took the arcs separating nothing from 4 to 1. Hiding an element counts as saying it is not part of the map: two hidden paths in `Border_Tracers` took the last of those away.
