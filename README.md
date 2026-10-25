# erpe-altera-map

**erpe-altera-map** is a small application for turning a vector world map given as an SVG file into GIS data, serialized as GeoJson and TopoJSON. It is specifically tailored for Atlas Altera and would probably need some adjustments to work with generic vector graphics.

The point of it is a **planar partition**: a border exists exactly once, and both countries either side of it are built from the very same coordinates. Nothing is unioned, buffered or snapped into place afterwards, because an overlay re-nodes what it is given and writes a new edge wherever it decides two are not quite the same. That is what makes the output safe to turn into TopoJSON, and it is why the pipeline below is built the way it is rather than as a sequence of geometry operations.

## Algorithm

The input is not a partition. The drawn country shapes overlap, leave gaps, and each carries its own version of every border it shares. The pipeline throws those versions away and rebuilds the map from a graph of **arcs** — lines between **nodes** where three or more of them meet — so that a border is one arc with two countries referring to it.

| Step | What happens |
|------| --- |
| **1** | Read the drawing: approximate its curves as lines, leave out linework too short to matter and anything the author has hidden, take the `Border_Tracers` and `Substate_Tracers` layers as arcs on shared nodes, and build the drawn country shapes as polygons. |
| **2** | Cut out the stretches a tracer draws twice: whole lines drawn over each other, and needles where one doubles back along itself. |
| **3** | Union the country shapes into the landmass and take its outline as the coast — the unitary states and the substates, since a federation is made of its substates and its own shape only says which country they make up. Each hole, divided first where a gap along a border runs into a lake, is a lake or a gap between shapes, told apart by its **hydraulic radius** — area over perimeter, which measures thinness and smallness at once. Gaps are filled, lakes are kept as their own kind of arc, and the borders drawn across either are cut back to the water's edge. |
| **4** | Weld the loose tracer ends: ends within 0.5 units of each other become one node, and an end within 3 units of another arc is threaded into it as a node. |
| **5** | Cut the land at the antimeridian: the rim, traced from the projection, is noded in with everything else and kept only where it crosses land. Node the remaining crossings, then trace every face by walking half-edges — at each node, one step clockwise from the way back. Arcs that cannot be traced are removed first: arcs the noding grid cannot represent, one line drawn twice as two arcs, overshoots dangling past a crossing, and arcs that run out and back along their own line. Each face drawn beyond the rim is then taken round to the other side of the map, or left out where its far side is ground already drawn. |
| **6** | Assign each face to a unitary state or a substate by how much of it that shape covers; a federation is the faces of its substates. A face bounded only by lake shore is water, and a face nobody claims is absorbed into the neighbour it shares the most border with. |
| **7** | Dissolve each country by **walking the arcs** that bound its faces, not by unioning them. The rim is never dissolved, so a country it crosses comes out as one polygon either side of it, meeting at exactly ±180 — the antimeridian cut RFC 7946 asks for — and no longitude lies outside ±180. Unproject, turn every ring the way RFC 7946 asks, simplify for the coarse outputs, and write the GeoJson and the topology. |
| **8** | Verify: count every segment of the output and check that each inland one is used exactly twice, once from each side and in opposite directions, with the same coordinates. Then count the land and the economic zones together, every coast now used twice, by the land and by the zone beside it; and check that no shape of either lies inside one of the other. |

### Economic zones

Every unitary state and substate with a shore is given its exclusive economic zone: the water within 200 nautical miles of its shores, divided from its neighbours' by median lines. Lakes count as water, and a lake several countries share is divided between them; there is no size below which an islet goes without a zone. A federation's zone is its substates'. The zones are computed between the two assemblies of step 7, when every face still carries its unitary state or substate, and written with the land.

1. **Sample the shores** every kilometre along the sphere. Where a border meets the shore, pairs of samples are put ever closer to the node — 1 m, 2 m, 4 m and on, each pair exactly as far from it — so that the median line between the two countries leaves the shore where their border does; and one at every corner of the shore there.
2. **Take the Voronoi diagram of the samples on the sphere.** It is read off a Delaunay triangulation of their stereographic image, projected from the point of open ocean furthest from any shore. Where a shore's samples are spaced more than half as far apart as the nearest sample of another country, the shore is resampled more finely; channels under 50 m wide are reported as shapes that disagree about the shore instead.
3. **Read off the boundaries.** A median line is an edge of the diagram between samples of two countries. The outer limit is each sample's 370.4 km circle where it lies inside the sample's own cell — the identity that replaces a buffer. The pieces are chained into lines, each knowing which country lies on which side.
4. **Cut them back to the water** in the source map, and fix each median line to the land's own node where the two countries' shores meet, so that zone and land are bounded by the same point.
5. **Trace the sea** from those lines, the coasts and the antimeridian — the coasts being the land's own arcs — give each face the country its boundary speaks for, and assemble each zone by walking the arcs, as step 7 does the land. A coast is then one arc with the land on one side and the zone on the other.

The algorithms the pipeline rests on:

* [De Casteljau's algorithm](https://en.wikipedia.org/wiki/De_Casteljau%27s_algorithm) for approximating bezier curves in the vector file with lines, as GIS does not handle curves.
* The method described in [this StackOverflow answer](https://stackoverflow.com/a/31474580) for turning the self-intersecting polygons of the input into multiple non-self-intersecting ones.
* The multidimensional [Newton-Raphson method](https://en.wikipedia.org/wiki/Newton%27s_method) for approximating the inverse of the Winkel Tripel projection, as described in the paper _[A general algorithm for the inverse transformation of map projections using Jacobian matrices](https://web.archive.org/web/20141020111146/http://atlas.selcuk.edu.tr/paperdb/papers/130.pdf)_ by Ipbüker and Bildirici.
* [Visvalingam–Whyatt](https://en.wikipedia.org/wiki/Visvalingam%E2%80%93Whyatt_algorithm) for the coarse outputs, applied **once per arc** rather than once per polygon so that both sides of a border lose the same vertices.
* The [Voronoi diagram](https://en.wikipedia.org/wiki/Voronoi_diagram) of the shore samples for the economic zones, from a [Delaunay triangulation](https://en.wikipedia.org/wiki/Delaunay_triangulation) on the sphere: planar Delaunay of the [stereographic projection](https://en.wikipedia.org/wiki/Stereographic_projection), which takes circles to circles, with the samples put in along a [Hilbert curve](https://en.wikipedia.org/wiki/Hilbert_curve) so that each lands near the one before.

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
| `map.json` | the unitary states and the substates together, which is a partition of the land as well |
| `map-no-substates.json` | the national layer alone, which is a partition of the land |
| `map-only-substates.json` | the substate layer alone, likewise |
| `map-medium.json`, `map-low.json` | the same at simplification tolerances of 0.001 and 0.01 degrees |
| `map-zones.json`, `map-zones-medium.json`, `map-zones-low.json` | the exclusive economic zones, 200 nautical miles out to sea and over the lakes, divided by median lines where two countries' zones meet: one per unitary state and one per substate, at each detail level. With the land they are a partition of everything within 200 nautical miles of a shore, a coast written with the same coordinates on either side |
| `map.topojson`, `map-medium.topojson`, `map-low.topojson` | the national layer, the substate layer and the zones as one topology per detail level, sharing one set of arcs: `countries`, `substates` and `zones` |

The TopoJSON is not derived from the GeoJson by comparing coordinates — it falls out of the arc graph directly, which is what the graph was built for. The two agree about every country, including which way round each ring is wound.

With `--debug-svg`, seven further files are written **in the coordinates of the source map**, framed on its own view box, so that each can be opened on top of the input and on each other:

| File | Stage |
| --- | --- |
| `map-arcs-before.svg` | every arc of every kind, the drawn rim, and the antimeridian traced from the projection, after welding and before face tracing |
| `map-arcs-after.svg` | the same after tracing, with the crossings split and the untraceable arcs gone |
| `map-faces.svg` | everything the border network encloses, one path per face |
| `map-dropped.svg` | only the faces left out, including those beyond the rim whose far side is already drawn |
| `map-zones.svg` | the economic zones' median lines and limits as found on the sphere, over land as well as water |
| `map-water.svg` | the sea graph: the zone boundaries cut back to the water, the shores and the rim |
| `map-sea.svg` | the faces of the sea graph once the land's are taken out: the zones and the high seas |

The two arc files are both written because step 5 mints new arc identifiers as it splits arcs at their crossings: anything the later reports name by number is absent from the earlier file.

## Known limitations

* The source map is expected to be an Atlas Altera export. The projection is hardcoded to Winkel Tripel and the layer names above are not configurable.
