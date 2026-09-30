// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

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

    public string Id { get; } = id;

    public Polygon Polygon { get; set; } = Polygon.Empty;
}