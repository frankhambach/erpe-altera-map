// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Generic;

// The economic zones' boundaries as arcs in the source map's coordinates, cut back to the water:
// the graph they make, the boundary each arc was cut from, which says whose zone lies either side
// of it, and the nodes where a boundary was cut at the antimeridian, which the rim over water has
// to be cut at as well. Every arc runs the way its boundary does.
public sealed class ZoneGraph(
    ArcGraph graph,
    IReadOnlyDictionary<Arc, ZoneBoundary> boundaries,
    IReadOnlyCollection<Node> antimeridian)
{
    public IReadOnlyCollection<Node> Antimeridian { get; } = antimeridian;

    public IReadOnlyDictionary<Arc, ZoneBoundary> Boundaries { get; } = boundaries;

    public ArcGraph Graph { get; } = graph;
}
