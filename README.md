# erpe-altera-map

**erpe-altera-map** Is a small application for turning a vector world map given as SVG file into GIS data, serialized as GeoJson. It is specifically tailored for Atlas Altera and would probably need some adjustments to work with generic vector graphics.

The application uses several algorithms to achieve its goals:
* [De Casteljau's algorithm](https://en.wikipedia.org/wiki/De_Casteljau%27s_algorithm) for approximating bezier curves in the vector file with lines, as GIS does not handle curves.
* The method described in [this StackOverflow answer](https://stackoverflow.com/a/31474580) for turning self-intersecting polygons into multiple non-self-intersecting polygons.
* The multidimensional [Newton-Raphson-Method](https://en.wikipedia.org/wiki/Newton%27s_method) for approximating the inverse of the Winkel Tripel projection, as described in the paper _[A general algorithm for the inverse transformation of map projections using Jacobian matrices](https://web.archive.org/web/20141020111146/http://atlas.selcuk.edu.tr/paperdb/papers/130.pdf)_ by Ipbüker and Bildirici

It also requires [mapshaper](https://github.com/mbloch/mapshaper) to be installed. It is used to snap vertices and remove overlaps in GIS data.

## Usage

### convert

Converts an SVG world map into GeoJson. This expects the SVG file to contain an SVG group with the ID `Rim` containing the outline of the projected world map. Only shapes within SVG groups with IDs `Country_Shapes` or `Substate_Shapes` will be converted to GIS data.

```
erpe-altera-map convert --in <file> --out <file>
```

* `--in` The SVG file to read country shapes from.
* `--out` The path of the GeoJson file to write.