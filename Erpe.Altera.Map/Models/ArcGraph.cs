// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System;
using System.Collections.Generic;

using NetTopologySuite.Geometries;

public sealed class ArcGraph(CoordinateSequenceFactory coordinateSequenceFactory)
{
    // Monotonic rather than derived from the collection counts, so that identifiers stay
    // stable once welding starts merging nodes and removing them from the graph.
    private int nextArcId;

    private int nextNodeId;

    public IList<Arc> Arcs { get; } = new List<Arc>();

    public CoordinateSequenceFactory CoordinateSequenceFactory => coordinateSequenceFactory;

    public IList<Face> Faces { get; } = new List<Face>();

    public IList<Node> Nodes { get; } = new List<Node>();

    // Splits a full coordinate sequence into two nodes and the vertices between them. A
    // sequence that starts where it ends gets a single shared node, so a ring is already
    // closed before welding and polygonizing see it.
    public Arc AddArc(ArcKind kind, CoordinateSequence coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (coordinates.Count < 2)
        {
            throw new ArgumentException("An arc needs at least two coordinates.", nameof(coordinates));
        }

        Coordinate[] allCoordinates = coordinates.ToCoordinateArray();
        Node startNode = this.AddNode(allCoordinates[0]);
        Node endNode = allCoordinates[0].Equals2D(allCoordinates[^1]) ? startNode : this.AddNode(allCoordinates[^1]);

        return this.AddArc(kind, startNode, endNode, coordinateSequenceFactory.Create(allCoordinates[1..^1]));
    }

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
