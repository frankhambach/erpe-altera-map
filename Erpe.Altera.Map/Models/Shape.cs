// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using NetTopologySuite.Geometries;

[SuppressMessage(
    "Naming",
    "CA1724:Type names should not match namespaces",
    Justification = "There is not overlap in usage with the NetTopologySuite.Shape namespace.")]
public sealed class Shape(string id)
{
    public Shape(string id, Polygon polygon)
        : this(id)
    {
        this.Polygon = polygon;
    }

    public Shape(string id, Polygon polygon, IEnumerable<DirectedArc[]> rings)
        : this(id, polygon)
    {
        ArgumentNullException.ThrowIfNull(rings);

        this.Rings = [.. rings];
    }

    public string Id { get; } = id;

    public Polygon Polygon { get; set; } = Polygon.Empty;

    // The same boundary as Polygon, as the arcs it is made of: the shell first and then the
    // holes, matching Polygon's own rings one for one. This is what lets the topology be written
    // out as a topology - the arcs named once and referred to by index - rather than recovered
    // from the coordinates afterwards. Empty where a shape did not come from the arc graph.
    public IReadOnlyList<DirectedArc[]> Rings { get; } = [];
}