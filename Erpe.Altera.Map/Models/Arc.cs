// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// A single stretch of boundary, shared by the faces on either side of it. Coordinates
// holds only the vertices between the two nodes, so that welding a node never has to
// rewrite the arc's own geometry, and so that an arc has exactly one representation
// no matter which of its two faces is being assembled.
public sealed class Arc(int id, ArcKind kind, Node startNode, Node endNode, CoordinateSequence coordinates)
{
    public CoordinateSequence Coordinates { get; } = coordinates;

    public Node EndNode { get; set; } = endNode;

    public int Id { get; } = id;

    public bool IsClosed => this.StartNode == this.EndNode;

    public ArcKind Kind { get; } = kind;

    public Node StartNode { get; set; } = startNode;

    // The arc as a whole line: its two nodes with the interior vertices between them.
    // Segment i runs from index i to i + 1.
    public Coordinate[] GetFullCoordinates()
    {
        Coordinate[] interior = this.Coordinates.ToCoordinateArray();
        Coordinate[] full = new Coordinate[interior.Length + 2];
        full[0] = this.StartNode.Coordinate;
        interior.CopyTo(full, 1);
        full[^1] = this.EndNode.Coordinate;
        return full;
    }
}
