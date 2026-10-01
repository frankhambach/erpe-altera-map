// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// A point where arcs meet. The coordinate is mutable because welding moves nodes
// together; arcs reference nodes rather than copying their coordinates, so a weld
// updates every arc that ends there at once.
public sealed class Node(int id, Coordinate coordinate)
{
    public Coordinate Coordinate { get; set; } = coordinate;

    public int Id { get; } = id;
}
