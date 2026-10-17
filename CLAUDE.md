# CLAUDE.md

Guidance for working in this repository.

## What this is

A .NET 9 CLI (`erpe-altera-map`) that turns an Atlas Altera SVG world map into GeoJson and TopoJSON. One command, `convert`. See `README.md` for usage and output files.

The goal is a **planar partition**: a border exists once, and both countries either side of it are built from the very same `Coordinate` objects. Every design decision follows from that. In particular, geometry is **never** repaired by overlay — no `Union`, `Buffer(0)` or snapping to fix up shared edges — because a general overlay re-nodes its input and writes a new edge wherever it decides two are not quite the same. That one change, replacing a union with a walk over the arcs, took the unaccounted boundary from 5,567 segments to 2.

## `docs/topology-pipeline.md` is the spec, and it is load-bearing

It is a living document, not a historical record. It carries the phase-by-phase design, every tolerance with the measurement that chose it, **and the wrong turns** — things tried and abandoned, and figures that turned out to be stale and why. Several of its passages exist only to stop someone re-deriving a conclusion that measurement already overturned.

**Keep it in step with the code in the same change.** The established rhythm is two commits: the code, then the write-up. If a figure in the plan becomes stale, correct it rather than leaving it — more than once in this project a stale figure has sent work in the wrong direction (the "109 tied nodes" was 2 by the time anyone looked; the "821 rings wound against the convention" was all of them).

## Build, run, test

```bash
C:/Users/frank/.dotnet/dotnet.exe build Erpe.Altera.Map/Erpe.Altera.Map.csproj -v q --nologo
C:/Users/frank/.dotnet/dotnet.exe test Erpe.Altera.Map.Tests/Erpe.Altera.Map.Tests.csproj --nologo -v q
C:/Users/frank/.dotnet/dotnet.exe run --project Erpe.Altera.Map/Erpe.Altera.Map.csproj --no-build -- \
  convert --in "C:/Users/frank/Downloads/Altera_Data_Generating_Map(13).svg" --out <dir>/map.json
```

A full run is about two minutes. Write outputs to the scratchpad, not into the repo.

**The test suite has 44 pre-existing failures** — 43 `Unproject*` in `WinkelTripelProjectionServiceTests` and 1 `GetCoordinates*` in `SvgApproximationServiceTests`. They are unrelated to the topology work and have been failing throughout. **44 failing is the baseline**; compare against it rather than expecting zero, and say the total when reporting ("966 passing against the unchanged 44-failure baseline").

## Conventions that will fail the build if ignored

* **Analyzers are errors.** `Erpe.Analyzers` brings StyleCop and Sonar. The ones hit most often: SA1117 (parameters all on one line or each on its own), SA1202/SA1204 (public before private, static before instance), SA1508/SA1513 (blank lines around braces), SA1118 (no multi-line argument), SA1210 (usings ordered), S1144 (no unused private member — so a helper must be used in the same change that adds it), S1244 (no `==` on doubles; use `.Equals` or a range), S3358 (no nested ternary), S6605 (`Array.Exists` over `Any`), S125 (no commented-out code), CA1859, CA2007, CA1822/S2325, VSTHRD110, CA1861.
* **Nearly every `.cs` file is UTF-8 with a BOM** — 53 of the 59 tracked ones — and the working tree uses CRLF throughout (`core.autocrlf` is on, so git stores LF either way). The six without one are `AssemblyInfo.cs`, `IAssemblyService.cs`, `ICorrectionService.cs`, `ITracerService.cs`, `TracerService.cs` and `SvgApproximationServiceTests.cs`; they never had one, so leave them as they are rather than tidying them in passing, and give a new file the BOM. `sed -i` and most scripted edits strip the BOM, convert the line endings, or both — as does the `Write` tool, which emits neither BOM nor CRLF. Check with `file <path>` after writing a file any way other than `Edit`: a lost BOM is a real change, and an LF working-tree copy makes git warn on staging. Markdown has no BOM.
* **Commits are signed.** Verify with `git log --show-signature -1`.
* Comments carry the *reasoning and the measurement*, not a restatement of the code. This is the house style and it is why the comments are long. Match it.

## Architecture

`Program.cs` wires everything through `Microsoft.Extensions.DependencyInjection` and sets a global `PrecisionModel(10000.0)` — a 0.0001-unit noding grid, about 0.65 m on this map. `Commands/ConvertCommand.cs` is the pipeline; the services under `Services/` do the work behind `Contracts/I*.cs`.

Models worth knowing:

* `Arc` — interior coordinates plus two *mutable* `Node` references, so welding can re-point an end without rebuilding anything. `GetFullCoordinates()` is start node + interior + end node. `IsClosed` is `StartNode == EndNode`.
* `DirectedArc(Arc, bool IsReversed)` — a half-edge. `StartNode`/`EndNode` flip with `IsReversed`.
* `Face(Shell, Holes)` — rings of `DirectedArc`.
* `RingExtensions` — the single winding rule. `GetWinding()` answers 1 / −1 / 0, the 0 meaning "encloses nothing" within a band of 1e-12. **Both writers go through it** so neither can disagree with the other about a ring.

The recurring discriminator across the whole pipeline is the **hydraulic radius**, area over perimeter: it has units of length and scales linearly, so it measures thinness and smallness at once and needs no separate area test. It tells lakes from gaps, real rings from slivers, and collapsed stubs from corners.

## Invariants to preserve

Breaking any of these is a regression even if the build and tests pass:

1. **Borders are shared exactly.** `IVerificationService.VerifySharing` runs in-pipeline per layer; inland segments must be used twice from opposite sides. Simplification decides **once per arc** and drops by coordinate value, which is what carries one decision to both sides. Never simplify a ring.
2. **A coordinate is removed only if every arc carrying it offers it up.** A minimum point count is a promise about one arc, and removing by value breaks it wherever two arcs share a coordinate.
3. **No ring that enclosed ground may come out enclosing nothing.** `ISimplificationService` raises the arcs' floors and simplifies again until none does.
4. **Rings are wound for RFC 7946** — counter-clockwise shells, clockwise holes, decided in *output* coordinates. The source map has y running down the page, so unprojecting turns every ring over. NetTopologySuite's serializer is told `RingOrientationOption.DoNotModify` because it gets this wrong: handed 0 clockwise shells it writes 3.
5. **The GeoJson and the TopoJSON agree**, geometry and direction, for every country.

## Working style this project expects

**Measure; do not reason and move on.** This is the single most useful habit here, and it is in the plan repeatedly because reasoning has given the wrong answer several times — most sharply over the ring winding, where orienting before the serializer made the file *worse* and only instrumenting what went in against what came out found the cause. Before and after a change, run the pipeline and diff the outputs; `cmp` on the written files is often the clearest evidence, and "byte-identical" is a real result worth reporting.

The productive technique for a bad face or a missing country is the **window dump**: take the country, log every arc, node, departure angle and ring around it, and follow the traversal step by step until it turns the wrong way. Nine cases were read that way and gave nine different causes, none of them guessable from the report that announced it.

Two traps worth knowing:

* **Stale binaries.** A build error means the run you just did used the old binary. Check the build succeeded before trusting a measurement — a diagnostic once produced baseline-identical numbers that way.
* **Instrument, then revert.** Probes go in, get measured, and come out. Verify the tree afterwards with `git status --short`; the working tree normally carries only the user's own changes (a `stylecop.json` deletion and some untracked files).

## State and open items

Measured on `Altera_Data_Generating_Map_20261014.svg`, the export in use since mid-October. **The plan's figures are mostly the first export's and differ**; its preamble says which of its passages were measured against this one.

* **1225 countries** in the national layer and 1577 in the substate layer, every one of them with geometry.
* **7000 faces** traced, covering 5,854,540.0 of the 5,855,297.0 square units the coastline encloses. 2 unclaimed faces, both absorbed into their neighbours.
* **No arc separates nothing**, and no arc end is left loose.
* The sharing check, over 1,356,022 segments: 271,018 shared exactly from both sides, and **0 wound the same way, 0 used twice by one country, 0 used by three or more rings**. 3 segments (0.000%) are unaccounted for.
* **Every ring survives every tolerance**: 6505 of 6505 in the national layer at 0.001 and at 0.01, the floor raised on 4 arcs to keep it so.
* **0 clockwise shells and 0 counter-clockwise holes** — 6145 shells and 360 holes, all the right way round. Nothing reports this; it is checked by reading the written file.

The run finishes with **no errors**. Its 24 warnings are the per-case reports of what it corrected — 14 duplicated tracer stretches and 9 tracers cut back off the shore, each named with where it is — plus one that is the first open item below.

Open:

* **1 hole lands in the band the gap threshold was chosen to avoid**, between 0.10 and 0.19 on the area-to-length ratio, which was empty on the first export. Lakes and gaps are no longer cleanly separated on this one. The plan's 2c says what that figure rests on; it wants watching rather than nudging.
* **2 faces are claimed by more than one country**, both too small to be reported individually.
* **3 unaccounted segments**, 0.000% of the boundary. The first export's five were Agurania's 2 and Bruni's 3; these three have not been identified on this export.
* Two rings enclose nothing before simplification ever runs and are dropped on the way out: **Anian-23's shell** — the islet drawn *outside* the rim, which the unprojection's clamp at longitude 180 flattens onto the antimeridian — and **Namaquab-0's hole 1**. Anian's is a drawing error in the source map and no code here can repair it.
* **A hidden group would still be read.** The visibility rule in 1-bis asks the element, not its ancestors, and hiding a whole layer is what Illustrator does.

Known input errors are fixed in the source SVG by the user, not worked around here — duplicate Martinique and Iouanorre shapes went that way, as did an island present in the Substates layer but missing from the Country layer, and the tracer whose absence left Iveria and Ossetia sharing a face was drawn on 15 October, which took the arcs separating nothing from 4 to 1. Hiding an element counts as saying it is not part of the map: two hidden paths in `Border_Tracers` took the last of those away.

**The input file is edited in place under the same name.** It changed on 15 October between one measurement and the next, which invalidated a day's figures; check its timestamp before trusting a comparison against an earlier run.
