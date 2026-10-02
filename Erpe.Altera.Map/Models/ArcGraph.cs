// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

public sealed class ArcGraph
{
    // Monotonic rather than derived from the collection counts, so that identifiers stay
    // stable once welding starts merging nodes and removing them from the graph.
    private int nextArcId;

    private int nextNodeId;

    public IList<Arc> Arcs { get; } = new List<Arc>();

    public IList<Face> Faces { get; } = new List<Face>();

    public IList<Node> Nodes { get; } = new List<Node>();

    public Arc AddArc(ArcKind kind, Node startNode, Node endNode, CoordinateSequence coordinates)
    {
        Arc arc = new Arc(this.nextArcId++, kind, startNode, endNode, coordinates);
        this.Arcs.Add(arc);
        return arc;
    }

    public Node AddNode(Coordinate coordinate)
    {
        Node node = new Node(this.nextNodeId++, coordinate);
        this.Nodes.Add(node);
        return node;
    }
}
