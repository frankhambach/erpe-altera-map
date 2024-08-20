// <copyright file="Shape.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public sealed class Shape(string id)
{
    public Shape(string id, Polygon polygon)
        : this(id)
    {
        this.Polygon = polygon;
    }

    public string Id { get; } = id;

    public Polygon Polygon { get; set; }
}