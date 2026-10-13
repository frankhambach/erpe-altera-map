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

**The test suite has 44 pre-existing failures** — 43 `Unproject*` in `WinkelTripelProjectionServiceTests` and 1 `GetCoordinates*` in `SvgApproximationServiceTests`. They are unrelated to the topology work and have been failing throughout. **44 failing is the baseline**; compare against it rather than expecting zero, and say the total when reporting ("959 passing against the unchanged 44-failure baseline").

## Conventions that will fail the build if ignored

* **Analyzers are errors.** `Erpe.Analyzers` brings StyleCop and Sonar. The ones hit most often: SA1117 (parameters all on one line or each on its own), SA1202/SA1204 (public before private, static before instance), SA1508/SA1513 (blank lines around braces), SA1118 (no multi-line argument), SA1210 (usings ordered), S1144 (no unused private member — so a helper must be used in the same change that adds it), S1244 (no `==` on doubles; use `.Equals` or a range), S3358 (no nested ternary), S6605 (`Array.Exists` over `Any`), S125 (no commented-out code), CA1859, CA2007, CA1822/S2325, VSTHRD110, CA1861.
* **Every `.cs` file is UTF-8 with a BOM**, and the working tree uses CRLF throughout (`core.autocrlf` is on, so git stores LF either way). `sed -i` and most scripted edits strip the BOM, convert the line endings, or both. Check with `file <path>` after any scripted edit: a lost BOM is a real change to the file, and an LF working-tree copy makes git warn on staging. Markdown has no BOM.
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

Measured on the sample map: every country has geometry, no face is claimed by two countries, no arc separates nothing, no loose arc ends, 0 clockwise shells, every ring survives every tolerance, and the unaccounted boundary is 5 segments (0.000%).

Open, from the plan's "What is left":

* **2 substate segments used three times** — Capital Territory and Falkland, a self-touching ring. The one real defect the sharing check still finds.
* **5 unaccounted segments** — Agurania 2 over 0.029°, Bruni 3 forming a triangle of 8.5e-8 square degrees. The output's six decimals showing.
* **3 segments used twice by one country** — two needles inside sound shells and one pair of Nunangats shapes touching along a line that should probably be one shape. Coordinates inside real rings, so removing them would have to happen arc by arc.
* **4 faces claimed by no country** and reported each run as bordering none, so nothing can absorb them.
* Two rings enclose nothing before simplification ever runs and are dropped on the way out: Anian's islet drawn *outside* the rim, which the unprojection's clamp at longitude 180 flattens onto the antimeridian, and Namaquab's lake of 1.3e-7 square units. Anian's is a drawing error in the source map and no code here can repair it.

Known input errors are fixed in the source SVG by the user, not worked around here — duplicate Martinique and Iouanorre shapes went that way, and an island present in the Substates layer but missing from the Country layer is the current example.

## Branches

`main` carries everything above. **`coverage-check`** is a local branch holding `IVerificationService.VerifyCoverage` and a fifth debug file, `-uncovered.svg`, which compares the polygons the document was read as against the geometry assembled from them — the one check that does not go through the pipeline's own idea of the map. It found 71.66 square units of 5.77 million drawn and not in the output, 0.00124%, from three unrelated causes. It is unmerged and several commits behind.
