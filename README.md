# erpe-altera-map

**erpe-altera-map** is a small application for turning a vector world map given as an SVG file into GIS data, serialized as GeoJson and TopoJSON. It is specifically tailored for Atlas Altera and would probably need some adjustments to work with generic vector graphics.

The point of it is a **planar partition**: a border exists exactly once, and both countries either side of it are built from the very same coordinates. Nothing is unioned, buffered or snapped into place afterwards, because an overlay re-nodes what it is given and writes a new edge wherever it decides two are not quite the same. That is what makes the output safe to turn into TopoJSON, and it is why the pipeline below is built the way it is rather than as a sequence of geometry operations.

## Algorithm

The input is not a partition. The drawn country shapes overlap, leave gaps, and each carries its own version of every border it shares. The pipeline throws those versions away and rebuilds the map from a graph of **arcs** — lines between **nodes** where three or more of them meet — so that a border is one arc with two countries referring to it.

| Step | What happens |
|------| --- |
| **1** | Read the drawing: approximate its curves as lines, leave out linework too short to matter and anything the author has hidden, take the `Border_Tracers` and `Substate_Tracers` layers as arcs on shared nodes, and build the drawn country shapes as polygons. |
| **2** | Cut out the stretches a tracer draws twice: whole lines drawn over each other, and needles where one doubles back along itself. |
| **3** | Union the country shapes into the landmass and take its outline as the coast. Each hole is a lake or a gap between shapes, told apart by its **hydraulic radius** — area over perimeter, which measures thinness and smallness at once. Gaps are filled, lakes are kept as their own kind of arc, and the borders drawn across either are cut back to the water's edge. |
| **4** | Weld the loose tracer ends: ends within 0.5 units of each other become one node, and an end within 3 units of another arc is threaded into it as a node. |
| **5** | Cut the land at the antimeridian: the rim, traced from the projection, is noded in with everything else and kept only where it crosses land. Node the remaining crossings, then trace every face by walking half-edges — at each node, one step clockwise from the way back. Arcs that cannot be traced are removed first: arcs the noding grid cannot represent, one line drawn twice as two arcs, overshoots dangling past a crossing, and arcs that run out and back along their own line. Each face drawn beyond the rim is then taken round to the other side of the map, or left out where its far side is ground already drawn. |
| **6** | Assign each face to a country by how much of it that country covers. A face bounded only by lake shore is water, and a face nobody claims is absorbed into the neighbour it shares the most border with. |
| **7** | Dissolve each country by **walking the arcs** that bound its faces, not by unioning them. The rim is never dissolved, so a country it crosses comes out as one polygon either side of it, meeting at exactly ±180 — the antimeridian cut RFC 7946 asks for — and no longitude lies outside ±180. Unproject, turn every ring the way RFC 7946 asks, simplify for the coarse outputs, and write the GeoJson and the topology. |
| **8** | Verify: count every segment of the output and check that each inland one is used exactly twice, once from each side and in opposite directions, with the same coordinates. |

The algorithms the pipeline rests on:

* [De Casteljau's algorithm](https://en.wikipedia.org/wiki/De_Casteljau%27s_algorithm) for approximating bezier curves in the vector file with lines, as GIS does not handle curves.
* The method described in [this StackOverflow answer](https://stackoverflow.com/a/31474580) for turning the self-intersecting polygons of the input into multiple non-self-intersecting ones.
* The multidimensional [Newton-Raphson method](https://en.wikipedia.org/wiki/Newton%27s_method) for approximating the inverse of the Winkel Tripel projection, as described in the paper _[A general algorithm for the inverse transformation of map projections using Jacobian matrices](https://web.archive.org/web/20141020111146/http://atlas.selcuk.edu.tr/paperdb/papers/130.pdf)_ by Ipbüker and Bildirici.
* [Visvalingam–Whyatt](https://en.wikipedia.org/wiki/Visvalingam%E2%80%93Whyatt_algorithm) for the coarse outputs, applied **once per arc** rather than once per polygon so that both sides of a border lose the same vertices.

## Usage

### convert

Converts an SVG world map into GeoJson and TopoJSON. This expects the SVG file to contain an SVG group with the ID `Rim` containing the outline of the projected world map. The rim frames the map, fixing where the projection lies on the page; the antimeridian the land is cut along is traced from the projection itself, so editing the drawn rim does not move the cut. Only shapes within SVG groups with IDs `Country_Shapes` or `Substate_Shapes` will be converted to GIS data, and the border network comes from the groups `Border_Tracers` and `Substate_Tracers`.

```
erpe-altera-map convert --in <file> --out <file> [--debug-svg]
```

* `--in` The SVG file to read country shapes from.
* `--out` The path of the GeoJson file to write. Several files are written beside it, named after it.
* `--debug-svg` Also write the intermediate stages of the conversion beside the output as SVG.

Writing `--out map.json` produces:

| File | Contents |
| --- | --- |
| `map.json` | countries and substates together |
| `map-no-substates.json` | the national layer alone, which is a partition of the land |
| `map-only-substates.json` | the substate layer alone, likewise |
| `map-medium.json`, `map-low.json` | the same at simplification tolerances of 0.001 and 0.01 degrees |
| `map.topojson`, `map-medium.topojson`, `map-low.topojson` | both layers as one topology per detail level, sharing one set of arcs |

The TopoJSON is not derived from the GeoJson by comparing coordinates — it falls out of the arc graph directly, which is what the graph was built for. The two agree about every country, including which way round each ring is wound.

With `--debug-svg`, four further files are written **in the coordinates of the source map**, framed on its own view box, so that each can be opened on top of the input and on each other:

| File | Stage |
| --- | --- |
| `map-arcs-before.svg` | every arc of every kind, the drawn rim, and the antimeridian traced from the projection, after welding and before face tracing |
| `map-arcs-after.svg` | the same after tracing, with the crossings split and the untraceable arcs gone |
| `map-faces.svg` | everything the border network encloses, one path per face |
| `map-dropped.svg` | only the faces left out, including those beyond the rim whose far side is already drawn |

The two arc files are both written because step 5 mints new arc identifiers as it splits arcs at their crossings: anything the later reports name by number is absent from the earlier file.

## Known limitations

* The source map is expected to be an Atlas Altera export. The projection is hardcoded to Winkel Tripel and the layer names above are not configurable.
